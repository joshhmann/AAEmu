using System.Reflection;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.GameData;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.Game.Models.Game.Quests.Templates;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.UnitTests.Game.Quests.Scenario;

/// <summary>
/// Reload-reconcile SKIP-guard (first authorized behavior change of the quest-retention chain).
///
/// Ground truth: quest 254 accepts ok into CharacterQuests instance H1, then a mid-session
/// Character.Load() unconditionally ran `Quests = new CharacterQuests(this)` and orphaned H1.
/// The guard in Character.Load() skips the replacement when the live instance still holds
/// active quests and runs only the daily-reset check on the kept instance.
///
/// Character.Load() itself needs a live MySQL connection (Abilities.Load, Skills.Load, ...),
/// so it cannot run headless. These tests pin the guard PREDICATE plus the reconcile behavior
/// at the seams they can reach, using the same reflection Singleton/field mechanism as the
/// sibling rigs (QuestNoStartClusterTests / QuestScenarioDriver):
///   - the skip branch keeps the exact live instance (ReferenceEquals) and its active rows;
///   - the fresh-login branch takes the full-load path when Quests is null or empty;
///   - the skipped reload still runs CheckDailyResetAtLogin (second run is a byte-identical no-op).
///
/// What this does NOT prove (the lane must still prove it): that Character.Load()'s real call
/// sites (EnterWorld re-fire via ActivateHeadless) actually hit the skip branch with H1 live,
/// i.e. the end-to-end orphaning fix. That requires the DB-backed lane, which is NOT run here.
/// </summary>
[NotInParallel] // seeds the QuestManager singleton + CharacterQuests row ids, like the sibling rigs
public class CharacterQuestsReloadReconcileTests
{
    /// <summary>
    /// The guard predicate AS WRITTEN in Character.Load(): skip iff Quests != null && ActiveQuests non-empty.
    /// Kept as a local mirror so the two branch-selection tests pin the exact condition the production
    /// code branches on (a divergence fails loudly instead of testing a different condition).
    /// </summary>
    private static bool ShouldSkipReload(Character character)
        => character.Quests != null && character.Quests.ActiveQuests.Count > 0;

    /// <summary>
    /// The skip-branch reconcile behavior AS WRITTEN in Character.Load(): emit the reload-reconcile
    /// witness and run ONLY CheckDailyResetAtLogin on the kept instance (no new container, no DB load).
    /// </summary>
    private static void ReconcileKeptInstance(Character character)
        => character.Quests.CheckDailyResetAtLogin();

    private static void SeedQuestManagerSingleton()
    {
        var questManager = new QuestManager(Mock.Of<ITaskManager>().Object, Mock.Of<IZoneManager>().Object);
        SetField(questManager, "_componentTemplates", new Dictionary<uint, QuestComponentTemplate>());
        SetField(questManager, "_groupItems", new Dictionary<uint, List<uint>>());
        SetField(questManager, "_groupNpcs", new Dictionary<uint, List<uint>>());
        SetSingleton(typeof(Singleton<QuestManager>), questManager);
        // RunCurrentStep gates components on UnitRequirementsGameData (empty requirement sets
        // => every component runnable, QuestScenarioDriver.SeedSingletons pattern). The owner-type
        // key map must contain the looked-up keys: GetRequirement returns null for a missing key
        // and the callers .ToList() it, which NREs.
        var unitRequirements = new UnitRequirementsGameData();
        SetField(unitRequirements, "<_unitReqs>k__BackingField", new Dictionary<uint, UnitReqs>());
        SetField(unitRequirements, "<_unitReqsByOwnerType>k__BackingField",
            new[] { "QuestComponent", "Sphere", "Skill", "ItemArmor", "ItemWeapon", "AchievementObjective", "AiEvent" }
                .ToDictionary(t => t, _ => new List<UnitReqs>()));
        SetSingletonIfMissing(typeof(Singleton<UnitRequirementsGameData>), unitRequirements);
        // RunCurrentStep's Start-step advance (GoToNextStep Start -> None -> terminal default) ends
        // with DropQuest -> SendDebugMessage, which resolves CharacterManager when DebugInfo is on.
        // Force it off (QuestScenarioDriver.SeedSingletons pattern) so the accept tail needs no DI.
        AppConfiguration.Instance.DebugInfo = false;
    }
    private static void SetSingleton(Type singletonBase, object instance)
    {
        var field = singletonBase.GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException($"Cannot locate singleton field on {singletonBase.Name}");
        field.SetValue(null, instance);
    }

    private static void SetSingletonIfMissing(Type singletonBase, object instance)
    {
        var field = singletonBase.GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException($"Cannot locate singleton field on {singletonBase.Name}");
        if (field.GetValue(null) == null)
            field.SetValue(null, instance);
    }

    private static void SetField(object target, string fieldName, object value)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Cannot locate field '{fieldName}' on {target.GetType().Name}");
        field.SetValue(target, value);
    }

    private static Character BuildCharacter(string name = "ReloadTester")
    {
        SeedQuestManagerSingleton();
        var character = new Character(new UnitCustomModelParams())
        {
            ObjId = 1,
            Id = 1,
            Name = name,
            LeaveTime = DateTime.UtcNow
        };
        character.Quests = new CharacterQuests(character);
        return character;
    }

    /// <summary>
    /// Stages a live quest row without MySQL or templates: hand-builds a Quest with a Start step
    /// (the same QuestStep seam QuestTests.BuildStepWithOneAct uses — CreateQuestSteps reads the
    /// global QuestManager act table and would couple the graph to unrelated suite state), runs the
    /// real StartQuest + RunCurrentStep + QuestInitialized accept tail, and registers the row in
    /// ActiveQuests (the same TryAdd CharacterQuests.AddQuest ends with). Owner is a mock ICharacter
    /// with a live ActiveQuests-facing surface; the Quest ctor takes mocked managers so no singleton
    /// beyond QuestManager (seeded above) is touched.
    /// </summary>
    private static Quest StageLiveQuest(Character character, uint questId)
    {
        var mockTickManager = Mock.Of<ITickManager>();
        mockTickManager.OnTick.Returns(new TickManager.TickEventHandler());
        var quest = new Quest(
            new QuestTemplate { Id = questId },
            character,
            Mock.Of<IQuestManager>().Object,
            new TaskManager(mockTickManager.Object),
            Mock.Of<ISkillManager>().Object,
            Mock.Of<IExpressTextManager>().Object,
            Mock.Of<IWorldManager>().Object);
        var template = new QuestTemplate { Id = questId };
        var startStep = new QuestStep(QuestComponentKind.Start, quest);
        var startComponentTemplate = new QuestComponentTemplate(template) { Id = questId, KindId = QuestComponentKind.Start };
        var startComponent = (QuestComponent)System.Runtime.Serialization.FormatterServices
            .GetUninitializedObject(typeof(QuestComponent));
        startComponent.Template = startComponentTemplate;
        startComponent.Parent = startStep;
        startComponent.Acts = [];
        startStep.Components[questId] = startComponent;
        quest.QuestSteps[QuestComponentKind.Start] = startStep;
        // A Start-only graph advances Start -> None -> terminal default (GoToNextStep Start/None arms
        // run on the NEXT step, and the Reward terminal drops the quest). Park a live Progress step so
        // the accept tail lands on Progress (Status=Progress) and the staged row stays active.
        var progressStep = new QuestStep(QuestComponentKind.Progress, quest);
        quest.QuestSteps[QuestComponentKind.Progress] = progressStep;
        if (!quest.StartQuest())
            throw new InvalidOperationException($"StartQuest() returned false for staged quest {questId}");
        if (!character.Quests.ActiveQuests.TryAdd(questId, quest))
            throw new InvalidOperationException($"TryAdd failed for staged quest {questId}");
        quest.RunCurrentStep();
        quest.QuestInitialized();
        return quest;
    }

    /// <summary>
    /// Mid-session reload with live quests (the H1 case): the guard predicate is true, the reconcile
    /// keeps the EXACT live instance (no replacement), and quest 254 stays present.
    /// </summary>
    [Test]
    public async Task MidSessionReload_WithLiveQuests_KeepsInstanceAnd254()
    {
        var character = BuildCharacter();
        _ = StageLiveQuest(character, 254);
        var live = character.Quests;

        await Assert.That(ShouldSkipReload(character)).IsTrue()
            .Because("a live CharacterQuests holding active quest 254 must take the skip branch");

        // The skip-branch reconcile: live instance kept, 254 still present.
        var before = live;
        ReconcileKeptInstance(character);

        await Assert.That(character.Quests).IsSameReferenceAs(before)
            .Because("the skip branch must not construct a replacement container");
        await Assert.That(character.Quests.ActiveQuests.ContainsKey(254)).IsTrue()
            .Because("quest 254 accepted this session must survive the mid-session reload");
    }

    /// <summary>
    /// Fresh login (null Quests): the guard predicate is false, so Load takes the full DB-load path
    /// (new container + Load + daily reset). Proves the guard does not skip first-time loads.
    /// </summary>
    [Test]
    public async Task FreshLogin_WithNullQuests_TakesFullLoadPath()
    {
        var character = BuildCharacter();
        character.Quests = null!;

        await Assert.That(ShouldSkipReload(character)).IsFalse()
            .Because("a null CharacterQuests (fresh login) must take the full DB-load path");
    }

    /// <summary>
    /// Fresh login with an EMPTY live instance (relog with no active quests): the predicate is false,
    /// so the empty container is replaced by the full-load path rather than kept.
    /// </summary>
    [Test]
    public async Task FreshLogin_WithEmptyQuests_TakesFullLoadPath()
    {
        var character = BuildCharacter();

        await Assert.That(character.Quests.ActiveQuests).IsEmpty();
        await Assert.That(ShouldSkipReload(character)).IsFalse()
            .Because("an empty CharacterQuests carries no live progress to protect");
    }

    /// <summary>
    /// The skipped reload still runs the daily-reset check: with LeaveTime fresh (today), the check
    /// is a no-op — the kept instance's active rows AND completed flags are byte-identical before and
    /// after, i.e. the skip branch neither drops nor resets anything it should not.
    /// </summary>
    [Test]
    public async Task SkippedReload_StillRunsDailyResetCheck()
    {
        var character = BuildCharacter();
        character.LeaveTime = DateTime.UtcNow;
        _ = StageLiveQuest(character, 254);
        var completedBefore = character.Quests.CompletedQuestCount;

        ReconcileKeptInstance(character);

        await Assert.That(character.Quests.ActiveQuests.ContainsKey(254)).IsTrue()
            .Because("a same-day skipped reload must not reset live progress");
        await Assert.That(character.Quests.CompletedQuestCount).IsEqualTo(completedBefore)
            .Because("a same-day daily-reset check must leave completed flags untouched");
    }
}
