using System.Numerics;
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
using AAEmu.Game.Models.Game.World;
using Transform = AAEmu.Game.Models.Game.World.Transform.Transform;

namespace AAEmu.UnitTests.Game.Models.Game.Quests.Acts;

/// <summary>
/// QuestActCheckSphere is a "check" (condition) act, not an objective act: the loader
/// keeps ThisComponentObjectiveIndex = 0xFF, so RunAct must evaluate the owner's LIVE
/// position against the component's quest spheres (mirroring QuestActCheckGuard's
/// live-world-state check), and sphere enter/exit events must only re-request quest
/// evaluation — writing the Objectives array at index 0xFF crashed with
/// IndexOutOfRangeException (Objectives has MaxObjectiveCount = 5 entries).
/// </summary>
[ParallelLimiter<AAEmu.UnitTests.Game.Housing.SequentialParallelLimit>]
[NotInParallel] // touches shared statics (SphereQuestManager._sphereQuests + QuestManager singleton) — same convention as QuestScenarioTests
public class QuestActCheckSphereTests
{
    // Canonical 1.2 data: quest 1033 (기억과 쇠 골렘) is the ONLY quest_context whose
    // quest_acts references QuestActCheckSphere (11 detail rows, 10 orphaned):
    // component 5065 (Progress), quest_act_check_spheres id 45 -> sphere_id 945.
    private const uint QuestId = 1033;
    private const uint ComponentId = 5065;
    private const uint SphereDetailId = 945;

    private static readonly Vector3 SphereCenter = new(100, 200, 300);

    private Dictionary<uint, List<SphereQuest>> _previousSpheres;
    private object _previousQuestManager;

    [Before(Test)]
    public void SetUp()
    {
        // Quest construction walks QuestManager.Instance (QuestComponent ctor resolves
        // acts) — seed the singleton with an unloaded manager whose act lookup table is
        // empty (mirrors QuestScenarioDriver.SeedSingletons).
        var questManagerField = typeof(Singleton<QuestManager>).GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static);
        _previousQuestManager = questManagerField?.GetValue(null);
        var questManager = new QuestManager(Mock.Of<ITaskManager>().Object, Mock.Of<IZoneManager>().Object);
        SetField(questManager, "_componentTemplates", new Dictionary<uint, QuestComponentTemplate>());
        questManagerField?.SetValue(null, questManager);

        // Sphere lookup table is only populated by SphereQuestManager.Load() in
        // production — seed it directly (same rig as QuestScenarioDriver).
        var spheresField = typeof(SphereQuestManager).GetField("_sphereQuests", BindingFlags.NonPublic | BindingFlags.Static);
        _previousSpheres = (Dictionary<uint, List<SphereQuest>>)spheresField?.GetValue(null);
        spheresField?.SetValue(null, new Dictionary<uint, List<SphereQuest>>
        {
            [ComponentId] = [new SphereQuest { QuestId = QuestId, ComponentId = ComponentId, Xyz = SphereCenter, Radius = 5f }]
        });
    }

    [After(Test)]
    public void TearDown()
    {
        var spheresField = typeof(SphereQuestManager).GetField("_sphereQuests", BindingFlags.NonPublic | BindingFlags.Static);
        spheresField?.SetValue(null, _previousSpheres);
        var questManagerField = typeof(Singleton<QuestManager>).GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static);
        questManagerField?.SetValue(null, _previousQuestManager);
    }

    /// <summary>
    /// Builds the act + a real Quest owned by a real Character attached to a fresh
    /// WorldInstance with a seeded SphereQuestManager, plus the QuestAct wrapper used
    /// by the event handlers. The Quest's QuestInitialized() has run, so
    /// RequestEvaluation() actually reaches the (mock) quest manager.
    /// </summary>
    private static (QuestActCheckSphere Act, QuestAct QuestAct, Quest Quest, Mock<IQuestManager> QuestManagerMock, Character Character, WorldInstance World) Setup()
    {
        var questTemplate = new QuestTemplate
        {
            Id = QuestId,
            Components = new Dictionary<uint, QuestComponentTemplate>()
        };
        var componentTemplate = new QuestComponentTemplate(questTemplate)
        {
            Id = ComponentId,
            KindId = QuestComponentKind.Progress
        };
        questTemplate.Components[ComponentId] = componentTemplate;

        var character = new Character(new UnitCustomModelParams())
        {
            ObjId = 1,
            Id = 1,
            Name = "Tester"
        };
        character.Transform = new Transform(character, null, Vector3.Zero);

        var worldTemplate = new WorldTemplate
        {
            Id = 1,
            Name = "test_world",
            ZoneKeys = [],
            CellX = 2,
            CellY = 2,
            ZoneKeyByRegions = new uint[32, 32]
        };
        var world = new WorldInstance(worldTemplate, 0, true, 1);
        world.SphereQuestManager = new SphereQuestManager(world);
        // ParentWorld property setter routes through WorldManager.Instance (not
        // available in unit tests) — set the backing field directly (same as the
        // QuestActCheckGuardTests rig).
        var parentWorldField = typeof(GameObject).GetField("_parentWorld", BindingFlags.NonPublic | BindingFlags.Instance);
        parentWorldField?.SetValue(character, world);

        var mockQuestManager = Mock.Of<IQuestManager>();
        var mockTickManager = Mock.Of<ITickManager>();
        mockTickManager.OnTick.Returns(new TickManager.TickEventHandler());
        var taskManagerInstance = new TaskManager(mockTickManager.Object);

        var quest = new Quest(
            questTemplate,
            character,
            mockQuestManager.Object,
            taskManagerInstance,
            Mock.Of<ISkillManager>().Object,
            Mock.Of<IExpressTextManager>().Object,
            Mock.Of<IWorldManager>().Object);
        quest.QuestInitialized();

        var act = new QuestActCheckSphere(componentTemplate) { DetailId = 45, SphereId = SphereDetailId };
        var step = quest.QuestSteps[QuestComponentKind.Progress];
        var component = step.Components[ComponentId];
        var questAct = new QuestAct(component, act);
        component.Acts.Add(questAct);

        return (act, questAct, quest, mockQuestManager, character, world);
    }

    private static void SetField(object target, string fieldName, object value)
    {
        typeof(QuestManager).GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance)?.SetValue(target, value);
    }

    #region RunAct — live sphere state checks

    [Test]
    public async Task RunAct_OwnerInsideSphere_ReturnsTrue()
    {
        // Arrange — owner standing exactly on the sphere center
        var (act, _, quest, _, character, _) = Setup();
        character.Transform = new Transform(character, null, SphereCenter);

        // Act
        var result = act.RunAct(quest, null, 0);

        // Assert
        // Fail-before: old code returned currentObjectiveCount > 0 and the act has
        // ThisComponentObjectiveIndex = 0xFF, so RunAct always saw count 0 -> false.
        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task RunAct_OwnerOutsideSphere_ReturnsFalse()
    {
        // Arrange — owner at origin, sphere centered at (100,200,300) r=5
        var (act, _, quest, _, _, _) = Setup();

        // Act
        var result = act.RunAct(quest, null, 0);

        // Assert
        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task RunAct_NoSphereDataForComponent_ReturnsFalse()
    {
        // Arrange — no spheres registered for this component (empty lookup table)
        var (act, _, quest, _, _, _) = Setup();
        var spheresField = typeof(SphereQuestManager).GetField("_sphereQuests", BindingFlags.NonPublic | BindingFlags.Static);
        spheresField?.SetValue(null, new Dictionary<uint, List<SphereQuest>>());

        // Act
        var result = act.RunAct(quest, null, 0);

        // Assert — must not throw and must not pass
        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task RunAct_NullOwnerTransform_ReturnsFalse()
    {
        // Arrange — owner without a Transform (as rigged by the scenario harness)
        var (act, _, quest, _, character, _) = Setup();
        character.Transform = null;

        // Act
        var result = act.RunAct(quest, null, 0);

        // Assert — defensive: no NRE, no pass
        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task RunAct_OwnerIsNotCharacter_ReturnsFalse()
    {
        // Arrange — a non-Character owner (interface mock)
        var (act, _, quest, _, _, _) = Setup();
        quest.Owner = Mock.Of<ICharacter>().Object;

        // Act
        var result = act.RunAct(quest, null, 0);

        // Assert
        await Assert.That(result).IsFalse();
    }

    #endregion

    #region OnEnterSphere / OnExitSphere — re-evaluation, never Objectives[0xFF]

    [Test]
    public async Task OnEnterSphere_MatchingSphere_RequestsEvaluation_NoObjectiveWrite()
    {
        // Arrange
        var (act, questAct, quest, questManagerMock, _, _) = Setup();
        var args = new OnEnterSphereArgs
        {
            SphereQuest = new SphereQuest { QuestId = QuestId, ComponentId = ComponentId },
            OldPosition = Vector3.Zero,
            NewPosition = SphereCenter
        };

        // Act — fail-before: old code did SetObjective(questAct, 1) with
        // ThisComponentObjectiveIndex = 0xFF -> IndexOutOfRangeException
        act.OnEnterSphere(questAct, null, args);

        // Assert — the step re-evaluates so RunAct can check the live position ...
        questManagerMock.EnqueueEvaluation(Any<Quest>()).WasCalled(Times.Once);
        // ... and no objective counter was written anywhere (check acts keep no objectives)
        await Assert.That(quest.Objectives.All(o => o == 0)).IsTrue();
    }

    [Test]
    public async Task OnEnterSphere_WrongComponent_DoesNotRequestEvaluation()
    {
        // Arrange — sphere event for a different component of the same quest
        var (act, questAct, _, questManagerMock, _, _) = Setup();
        var args = new OnEnterSphereArgs
        {
            SphereQuest = new SphereQuest { QuestId = QuestId, ComponentId = 9999 },
            OldPosition = Vector3.Zero,
            NewPosition = SphereCenter
        };

        // Act
        act.OnEnterSphere(questAct, null, args);

        // Assert
        questManagerMock.EnqueueEvaluation(Any<Quest>()).WasCalled(Times.Never);
    }

    [Test]
    public async Task OnExitSphere_MatchingSphere_RequestsEvaluation_NoObjectiveWrite()
    {
        // Arrange
        var (act, questAct, quest, questManagerMock, _, _) = Setup();
        var args = new OnExitSphereArgs
        {
            SphereQuest = new SphereQuest { QuestId = QuestId, ComponentId = ComponentId },
            OldPosition = SphereCenter,
            NewPosition = Vector3.Zero
        };

        // Act — fail-before: old code did SetObjective(questAct, 0) -> same 0xFF crash
        act.OnExitSphere(questAct, null, args);

        // Assert
        questManagerMock.EnqueueEvaluation(Any<Quest>()).WasCalled(Times.Once);
        await Assert.That(quest.Objectives.All(o => o == 0)).IsTrue();
    }

    #endregion
}
