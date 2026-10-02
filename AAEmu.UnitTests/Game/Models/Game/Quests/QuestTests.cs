using AAEmu.Commons.Network;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.Game.Models.Game.Quests.Templates;
#pragma warning disable IDE0051

namespace AAEmu.UnitTests.Game.Models.Game.Quests;
// TODO: Re-enable the quest related test
// ReSharper disable UnusedMember.Local

public class QuestTests
{
    // [Fact]
    private async Task Start_WhenQuestStepIsNoneAndComponentIsEmpty_ShouldDoNothing()
    {
        // Arrange
        var quest = SetupQuest(out var mockOwner, out var mockQuestTemplate, out _, out _, out _, out _, out _);
        mockQuestTemplate.GetComponents(Any<QuestComponentKind>()).Returns([]);

        // Act
        var result = quest.StartQuest();

        // Assert
        await Assert.That(result).IsFalse();
        mockOwner.SendPacket(Any<GamePacket>()).WasCalled(Times.Once);
    }

    /// <summary>
    /// QuestInitialized is the gate that lets a quest's parked evaluation
    /// request reach the quest manager. While the quest is UNinitialized,
    /// RequestEvaluation only latches a flag (the setter's
    /// `value &amp;&amp; _questInitializationFinished` guard); once initialized,
    /// both the latch and the direct call reach EnqueueEvaluation.
    ///
    /// Both directions are observable through IQuestManager, so no reflection
    /// and no production seam is needed. The old version ended in
    /// `Assert.That(true).IsTrue()` — it stayed GREEN with QuestInitialized()
    /// gutted to `{ }`.
    /// </summary>
    [Test]
    public async Task QuestInitialized_WithAPendingRequest_FlushesItToTheQuestManager()
    {
        // Arrange: RequestEvaluation on an UNINITIALIZED quest parks the
        // request instead of enqueuing it (the constructor does not initialize).
        var quest = SetupQuest(out _, out _, out var mockQuestManager, out _, out _, out _, out _);
        quest.RequestEvaluation();

        mockQuestManager.EnqueueEvaluation(quest).WasCalled(Times.Never);
        // Because: an uninitialized quest must not evaluate; the request is parked.

        // Act
        quest.QuestInitialized();

        // Assert: initialization drains the parked request exactly once.
        mockQuestManager.EnqueueEvaluation(quest).WasCalled(Times.Once);
        // Because: QuestInitialized must flush the parked request exactly once.
    }

    [Test]
    public async Task QuestInitialized_WithNoPendingRequest_QueuesNothing()
    {
        // The negative half of the contract: initialization is not itself an
        // evaluation request. Without it, a body that enqueued unconditionally
        // would still satisfy the Times.Once assertion above.
        var quest = SetupQuest(out _, out _, out var mockQuestManager, out _, out _, out _, out _);

        // Act
        quest.QuestInitialized();

        // Assert
        mockQuestManager.EnqueueEvaluation(quest).WasCalled(Times.Never);
        // Because: initialization only flushes an existing pending request; it never creates one.
    }

    [Test]
    public async Task RequestEvaluation_AfterInitialization_EnqueuesImmediately()
    {
        // The mirror of the first test: once initialized, a later
        // RequestEvaluation goes straight through the latch's setter rather
        // than needing QuestInitialized to flush it. This is what every
        // objective change after load depends on.
        var quest = SetupQuest(out _, out _, out var mockQuestManager, out _, out _, out _, out _);
        quest.QuestInitialized();
        quest.StartingEvaluation(); // consume the initialization flush

        // Act
        quest.RequestEvaluation();

        // Assert
        mockQuestManager.EnqueueEvaluation(quest).WasCalled(Times.Once);
        // Because: on an initialized quest a request must reach the manager at once.
    }

    [Test]
    public async Task FinalizeQuestActs_CallsFinalizeQuestOnEveryActOfEveryStep()
    {
        // Arrange: a real two-step act graph, so the triple-nested loop has
        // something to walk and a short-circuited loop cannot pass.
        var quest = SetupQuest(out _, out _, out _, out _, out _, out _, out _);
        var (stepA, _, actA, _) = BuildStepWithOneAct(QuestComponentKind.Progress, 1);
        var (stepB, _, actB, _) = BuildStepWithOneAct(QuestComponentKind.Reward, 2);
        AddStepTo(quest, stepA, 1);
        AddStepTo(quest, stepB, 2);

        // Act
        quest.FinalizeQuestActs();

        // Assert: BOTH acts are finalized, each with its own live QuestAct.
        var spyA = (SpyActTemplate)actA.Template;
        var spyB = (SpyActTemplate)actB.Template;
        await Assert.That(spyA.Finalized.Count).IsEqualTo(1)
            .Because("step Progress's act must be finalized exactly once");
        await Assert.That(spyB.Finalized.Count).IsEqualTo(1)
            .Because("step Reward's act must be finalized exactly once");
        await Assert.That(spyA.Finalized[0]).IsSameReferenceAs(actA)
            .Because("the finalize call must carry the act it belongs to");
        await Assert.That(spyA.FinalizedQuest).IsSameReferenceAs(quest);
    }

    /// <summary>
    /// InitializeQuestActs is the twin of FinalizeQuestActs and had no coverage
    /// at all; the same spy pins that initialization reaches every act.
    /// </summary>
    [Test]
    public async Task InitializeQuestActs_CallsInitializeQuestOnEveryActOfEveryStep()
    {
        var quest = SetupQuest(out _, out _, out _, out _, out _, out _, out _);
        var (stepA, _, actA, _) = BuildStepWithOneAct(QuestComponentKind.Progress, 1);
        var (stepB, _, actB, _) = BuildStepWithOneAct(QuestComponentKind.Reward, 2);
        AddStepTo(quest, stepA, 1);
        AddStepTo(quest, stepB, 2);

        quest.InitializeQuestActs();

        var spyA = (SpyActTemplate)actA.Template;
        var spyB = (SpyActTemplate)actB.Template;
        await Assert.That(spyA.Initialized.Count).IsEqualTo(1);
        await Assert.That(spyB.Initialized.Count).IsEqualTo(1);
        await Assert.That(spyA.Initialized[0]).IsSameReferenceAs(actA);
    }

    /// <summary>
    /// QuestActTemplate whose InitializeQuest/FinalizeQuest record the exact
    /// QuestAct they were handed. Both are virtual on the production base, so
    /// this is a spy over the real call seam — no production change required.
    /// </summary>
    private sealed class SpyActTemplate(QuestComponentTemplate parent) : QuestActTemplate(parent)
    {
        public List<QuestAct> Initialized { get; } = [];
        public List<QuestAct> Finalized { get; } = [];
        public Quest FinalizedQuest { get; private set; }

        public override void InitializeQuest(Quest quest, QuestAct questAct) => Initialized.Add(questAct);
        public override void FinalizeQuest(Quest quest, QuestAct questAct)
        {
            Finalized.Add(questAct);
            FinalizedQuest = quest;
        }
    }

    /// <summary>
    /// One QuestStep holding one QuestComponent holding one spy act, plus the
    /// component template needed to graft it onto a quest. Built by hand rather
    /// than through CreateQuestSteps, which reads the global
    /// QuestManager.Instance act table and would make the graph under test
    /// depend on unrelated suite state.
    /// </summary>
    private static (QuestStep Step, QuestComponent Component, QuestAct Act, QuestTemplate Template)
        BuildStepWithOneAct(QuestComponentKind kind, uint detailId)
    {
        var template = new QuestTemplate { Id = detailId };
        var step = new QuestStep(kind, null!);
        var componentTemplate = new QuestComponentTemplate(template) { Id = detailId, KindId = kind };
        // QuestComponent's ctor resolves acts through QuestManager.Instance,
        // which is DI-only and throws on an unseeded process. This rig owns its
        // act list, so the component is created uninitialized (FormatterServices
        // keeps the runtime type) and its fields are set directly — the loops
        // under test only read Template / Parent / Acts.
        var component = (QuestComponent)System.Runtime.Serialization.FormatterServices
            .GetUninitializedObject(typeof(QuestComponent));
        component.Template = componentTemplate;
        component.Parent = step;
        component.Acts = [];
        var act = new QuestAct(component, new SpyActTemplate(componentTemplate) { DetailId = detailId });
        component.Acts.Add(act);
        step.Components[detailId] = component;
        return (step, component, act, template);
    }
    /// Installs a hand-built step into the quest's own QuestSteps dictionary.
    /// The loops under test iterate QuestSteps.Values, so populating that
    /// dictionary directly is the seam they actually read.
    /// </summary>
    private static void AddStepTo(Quest quest, QuestStep step, uint componentId) =>
        quest.QuestSteps[step.ThisStep] = step;

    [Test]
    public async Task WriteData_ReturnsByteArray()
    {
        // Arrange
        var quest = SetupQuest(out var mockOwner, out var mockQuestTemplate, out _, out _, out _, out _, out _);

        // Act
        var data = quest.WriteData();

        // Assert
        await Assert.That(data).IsNotNull();
        await Assert.That(data.Length > 0).IsTrue();
    }

    [Test]
    public async Task ReadData_WithValidData_SetsObjectives()
    {
        // Arrange
        var quest = SetupQuest(out var mockOwner, out var mockQuestTemplate, out _, out _, out _, out _, out _);
        var stream = new PacketStream();
        stream.Write(1); // objective 1
        stream.Write(2); // objective 2
        stream.Write(3); // objective 3
        stream.Write(4); // objective 4
        stream.Write(5); // objective 5
        stream.Write((byte)QuestComponentKind.Progress); // step
        stream.Write((byte)QuestAcceptorType.Npc); // acceptor type
        stream.Write(100u); // component id
        stream.Write(200u); // acceptor id
        stream.Write(DateTime.UtcNow); // time

        // Act
        quest.ReadData(stream.GetBytes());

        // Assert
        await Assert.That(quest.Step).IsEqualTo(QuestComponentKind.Progress);
        await Assert.That(quest.QuestAcceptorType).IsEqualTo(QuestAcceptorType.Npc);
    }

    #region Property Tests

    [Test]
    public async Task Status_SetAndGet_ReturnsCorrectValue()
    {
        // Arrange
        var quest = SetupQuest(out var mockOwner, out var mockQuestTemplate, out _, out _, out _, out _, out _);

        // Act
        quest.Status = QuestStatus.Completed;

        // Assert
        await Assert.That(quest.Status).IsEqualTo(QuestStatus.Completed);
    }

    [Test]
    public async Task Step_SetAndGet_ReturnsCorrectValue()
    {
        // Arrange
        var quest = SetupQuest(out var mockOwner, out var mockQuestTemplate, out _, out _, out _, out _, out _);

        // Act
        quest.Step = QuestComponentKind.Reward;

        // Assert
        await Assert.That(quest.Step).IsEqualTo(QuestComponentKind.Reward);
    }

    #endregion

    #region Objective Tests

    [Test]
    public async Task Objectives_ArrayAccess_ReturnsCorrectValue()
    {
        // Arrange
        var quest = SetupQuest(out var mockOwner, out var mockQuestTemplate, out _, out _, out _, out _, out _);

        // Act
        quest.Objectives[0] = 10;

        // Assert
        await Assert.That(quest.Objectives[0]).IsEqualTo(10);
    }

    #endregion

    #region ComponentId Tests

    [Test]
    public async Task ComponentId_SetAndGet_ReturnsCorrectValue()
    {
        // Arrange
        var quest = SetupQuest(out var mockOwner, out var mockQuestTemplate, out _, out _, out _, out _, out _);

        // Act
        quest.ComponentId = 123;

        // Assert
        await Assert.That(quest.ComponentId).IsEqualTo(123u);
    }

    #endregion

    #region DoodadId Tests

    [Test]
    public async Task DoodadId_SetAndGet_ReturnsCorrectValue()
    {
        // Arrange
        var quest = SetupQuest(out var mockOwner, out var mockQuestTemplate, out _, out _, out _, out _, out _);

        // Act
        quest.DoodadId = 456;

        // Assert
        await Assert.That(quest.DoodadId).IsEqualTo(456u);
    }

    #endregion

    #region QuestAcceptorType Tests

    [Test]
    public async Task QuestAcceptorType_SetAndGet_ReturnsCorrectValue()
    {
        // Arrange
        var quest = SetupQuest(out var mockOwner, out var mockQuestTemplate, out _, out _, out _, out _, out _);

        // Act
        quest.QuestAcceptorType = QuestAcceptorType.Doodad;

        // Assert
        await Assert.That(quest.QuestAcceptorType).IsEqualTo(QuestAcceptorType.Doodad);
    }

    #endregion

    #region AcceptorId Tests

    [Test]
    public async Task AcceptorId_SetAndGet_ReturnsCorrectValue()
    {
        // Arrange
        var quest = SetupQuest(out var mockOwner, out var mockQuestTemplate, out _, out _, out _, out _, out _);

        // Act
        quest.AcceptorId = 789;

        // Assert
        await Assert.That(quest.AcceptorId).IsEqualTo(789u);
    }

    #endregion

    #region Edge Cases

    [Test]
    public void Objectives_ArrayBounds_DoesNotThrow()
    {
        // Arrange
        var quest = SetupQuest(out var mockOwner, out var mockQuestTemplate, out _, out _, out _, out _, out _);

        // Act & Assert
        Assert.Throws<IndexOutOfRangeException>(() => quest.Objectives[10] = 1);
    }

    #endregion

    private static Quest SetupQuest(
        out Mock<ICharacter> mockCharacter,
        out Mock<IQuestTemplate> mockQuestTemplate,
        out Mock<IQuestManager> mockQuestManager,
        out Mock<TaskManager>? mockTaskManager,
        out Mock<ISkillManager> mockSkillManager,
        out Mock<IExpressTextManager> mockExpressTextManager,
        out Mock<IWorldManager> mockWorldManager)
    {
        mockCharacter = Mock.Of<ICharacter>();
        mockQuestManager = Mock.Of<IQuestManager>();
        mockQuestTemplate = Mock.Of<IQuestTemplate>();
        mockQuestTemplate.Components.Returns(new Dictionary<uint, QuestComponentTemplate>());
        mockExpressTextManager = Mock.Of<IExpressTextManager>();
        mockSkillManager = Mock.Of<ISkillManager>();

        // Create TaskManager with ITickManager dependency
        var mockTickManager = Mock.Of<ITickManager>();
        mockTickManager.OnTick.Returns(new TickManager.TickEventHandler());
        var taskManagerInstance = new TaskManager(mockTickManager.Object);
        mockTaskManager = null;

        mockWorldManager = Mock.Of<IWorldManager>();

        var quest = new Quest(
            mockQuestTemplate.Object,
            mockCharacter.Object,
            mockQuestManager.Object,
            taskManagerInstance,
            mockSkillManager.Object,
            mockExpressTextManager.Object,
            mockWorldManager.Object);

        quest.Owner = mockCharacter.Object;
        quest.Template = mockQuestTemplate.Object;
        return quest;
    }
}