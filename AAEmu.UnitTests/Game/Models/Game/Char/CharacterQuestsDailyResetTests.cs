using System.Reflection;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Quests.Acts;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.Game.Models.Game.Quests.Templates;
using AAEmu.Game.Models.Game.Units;
using AAEmu.UnitTests.Game.Quests.Scenario;

namespace AAEmu.UnitTests.Game.Models.Game.Char;

/// <summary>
/// Cat-34 daily-reset fix (Option A, Josh 2026-08-06): QuestDetail.Task (6) joins the
/// daily-reset family so "오늘 할 일" quests (87 quests, detail_id=6, REPEATABLE='f',
/// LEVEL 1-10) clear their completed flag at the midnight reset and become
/// re-acceptable — true 1.2 daily semantics.
///
/// Root cause (Mai, CAT34_INVESTIGATION.md / t_4126aa58):
///   - Re-accept gate CharacterQuests.AddQuest refuses completed quests with
///     Repeatable==false (CharacterQuests.cs:107-120).
///   - The ONLY auto-clear path is ResetDailyQuests -> ResetQuests, which clears
///     ONLY QuestDetail Daily/DailyGroup/DailyHunt/DailyLivelihood (7/12/10/11).
///     cat-34 quests are detail_id=6 (Task), so their flags were permanent.
///
/// The fix is a membership change: QuestDetail.Task joins the reset family array.
/// Everything else (the gate, Repeatable semantics, the 7/12/10/11 family) is
/// intentionally untouched — the tests below pin that contract.
///
/// Rig: synthetic QuestTemplates registered straight into the seeded QuestManager
/// (DetailId + Repeatable set explicitly), real Character + CharacterQuests, and
/// the mock-backed DI singletons the AddQuest path resolves (SkillManager,
/// WorldManager — same seeding PlayerbotPilotRig uses). Templates carry a Start
/// component whose QuestActConAcceptNpc can never match (NpcId 999999), so an
/// accepted quest parks in Start instead of auto-completing.
/// </summary>
[NotInParallel] // touches shared singletons (QuestManager + DI) — same convention as QuestScenarioTests
public class CharacterQuestsDailyResetTests
{
    // Synthetic quest ids (block 95): 6101..6107 share one CompletedQuest block.
    private const uint Cat34QuestId = 6101;          // DetailId=Task(6), Repeatable=false  — the fix target
    private const uint DailyQuestId = 6102;          // DetailId=Daily(7), Repeatable=false  — regression guard
    private const uint DailyHuntQuestId = 6103;      // DetailId=DailyHunt(10)              — regression guard
    private const uint DailyLivelihoodQuestId = 6104; // DetailId=DailyLivelihood(11)        — regression guard
    private const uint DailyGroupQuestId = 6105;     // DetailId=DailyGroup(12)             — regression guard
    private const uint RepeatableTaskQuestId = 6106; // DetailId=Task(6), Repeatable=true   — gate unchanged guard
    private const uint NormalQuestId = 6107;         // DetailId=Normal(1), Repeatable=false — family-not-broadened guard

    private Character _character;

    [Before(Test)]
    public void SetUp()
    {
        // Base singleton rig (QuestManager with empty tables, ItemManager,
        // QuestIdManager, TeamManager, TaskManager, ExperienceManager,
        // AccountManager, empty UnitRequirementsGameData).
        QuestScenarioDriver.SeedSingletons();

        // AddQuest -> new Quest(template, owner) resolves SkillManager.Instance /
        // WorldManager.Instance (DI singletons with no parameterless ctor) — seed
        // mock-backed instances (same as PlayerbotPilotRig.SeedPilotSingletons).
        SetSingleton(typeof(Singleton<SkillManager>),
            new SkillManager(Mock.Of<IAnimationManager>().Object, Mock.Of<IPlotManager>().Object));
        SetSingleton(typeof(Singleton<WorldManager>),
            new WorldManager(
                Mock.Of<ITickManager>().Object,
                Mock.Of<IWorldIdManager>().Object,
                new Lazy<IZoneManager>(() => Mock.Of<IZoneManager>().Object),
                new Lazy<IIndunManager>(() => Mock.Of<IIndunManager>().Object),
                new Lazy<IFamilyManager>(() => Mock.Of<IFamilyManager>().Object)));

        // Real Character + CharacterQuests (mirrors QuestScenarioDriver.BuildQuest).
        _character = new Character(new UnitCustomModelParams())
        {
            ObjId = 1,
            Id = 1,
            Name = "Cat34Tester",
            Level = 1
        };
        _character.Quests = new CharacterQuests(_character);

        // Register the synthetic templates: every quest gets a Start component with
        // a never-matching QuestActConAcceptNpc, so StartQuest succeeds but the quest
        // parks in Start (no auto-complete chain to Reward).
        RegisterQuest(Cat34QuestId, QuestDetail.Task, repeatable: false);
        RegisterQuest(DailyQuestId, QuestDetail.Daily, repeatable: false);
        RegisterQuest(DailyHuntQuestId, QuestDetail.DailyHunt, repeatable: false);
        RegisterQuest(DailyLivelihoodQuestId, QuestDetail.DailyLivelihood, repeatable: false);
        RegisterQuest(DailyGroupQuestId, QuestDetail.DailyGroup, repeatable: false);
        RegisterQuest(RepeatableTaskQuestId, QuestDetail.Task, repeatable: true);
        RegisterQuest(NormalQuestId, QuestDetail.Normal, repeatable: false);
    }

    /// <summary>
    /// The fix: a completed cat-34 quest (DetailId=Task/6, REPEATABLE='f') must have
    /// its completed flag cleared by the midnight daily reset, and must then be
    /// re-acceptable. FAIL-BEFORE: pre-fix the flag survives the reset (Task not in
    /// the family) and re-accept is refused.
    /// </summary>
    [Test]
    public async Task ResetDailyQuests_TaskDetailCompleted_ClearsFlagAndReaccepts()
    {
        // Completed yesterday, like a real cat-34 daily from the previous day.
        _character.Quests.SetCompletedQuestFlag(Cat34QuestId, true);
        await Assert.That(_character.Quests.HasQuestCompleted(Cat34QuestId)).IsTrue();

        // Same-day re-accept must still be refused (gate untouched).
        var sameDayReAccept = _character.Quests.AddQuest(Cat34QuestId);
        await Assert.That(sameDayReAccept, "completed REPEATABLE='f' quest must be refused same-day (gate unchanged)").IsFalse();

        // Midnight reset (QuestDailyResetTask / CheckDailyResetAtLogin path).
        _character.Quests.ResetDailyQuests(true);

        // THE FIX: the detail-6 flag clears at reset.
        await Assert.That(_character.Quests.HasQuestCompleted(Cat34QuestId),
            "Task(6) completed flag must clear at the daily reset (cat-34 true daily semantics)").IsFalse();

        // Re-accept succeeds — quest actually starts.
        var reAccepted = _character.Quests.AddQuest(Cat34QuestId);
        await Assert.That(reAccepted, "cat-34 quest must be re-acceptable after the daily reset").IsTrue();
        await Assert.That(_character.Quests.HasQuest(Cat34QuestId), "re-accepted quest must be active").IsTrue();
    }

    /// <summary>
    /// Regression guard: the existing daily family (7/12/10/11) must keep clearing at
    /// the reset — the fix adds Task(6), never removes family members.
    /// </summary>
    [Test]
    public async Task ResetDailyQuests_DailyFamilyCompleted_AllFlagsCleared()
    {
        _character.Quests.SetCompletedQuestFlag(DailyQuestId, true);
        _character.Quests.SetCompletedQuestFlag(DailyHuntQuestId, true);
        _character.Quests.SetCompletedQuestFlag(DailyLivelihoodQuestId, true);
        _character.Quests.SetCompletedQuestFlag(DailyGroupQuestId, true);

        _character.Quests.ResetDailyQuests(true);

        await Assert.That(_character.Quests.HasQuestCompleted(DailyQuestId), "Daily(7) must clear").IsFalse();
        await Assert.That(_character.Quests.HasQuestCompleted(DailyHuntQuestId), "DailyHunt(10) must clear").IsFalse();
        await Assert.That(_character.Quests.HasQuestCompleted(DailyLivelihoodQuestId), "DailyLivelihood(11) must clear").IsFalse();
        await Assert.That(_character.Quests.HasQuestCompleted(DailyGroupQuestId), "DailyGroup(12) must clear").IsFalse();
    }

    /// <summary>
    /// Guard: REPEATABLE='t' quests keep re-accepting WITHOUT a reset — the gate
    /// (Repeatable==false refusal) is untouched by the fix.
    /// </summary>
    [Test]
    public async Task AddQuest_RepeatableTaskCompleted_ReacceptsWithoutReset()
    {
        _character.Quests.SetCompletedQuestFlag(RepeatableTaskQuestId, true);

        // Same-day re-accept of a REPEATABLE='t' quest must succeed with no reset.
        var reAccepted = _character.Quests.AddQuest(RepeatableTaskQuestId);
        await Assert.That(reAccepted, "REPEATABLE='t' quest must re-accept without a reset (gate unchanged)").IsTrue();
        await Assert.That(_character.Quests.HasQuest(RepeatableTaskQuestId)).IsTrue();
    }

    /// <summary>
    /// Guard: a non-repeatable quest OUTSIDE the daily family (Normal/1) must NOT be
    /// cleared by the reset — the fix does not broaden the family beyond Task(6).
    /// </summary>
    [Test]
    public async Task ResetDailyQuests_NormalDetailCompleted_FlagSurvives()
    {
        _character.Quests.SetCompletedQuestFlag(NormalQuestId, true);

        _character.Quests.ResetDailyQuests(true);

        await Assert.That(_character.Quests.HasQuestCompleted(NormalQuestId),
            "Normal(1) is not in the daily-reset family — flag must survive").IsTrue();
        var reAccepted = _character.Quests.AddQuest(NormalQuestId);
        await Assert.That(reAccepted, "completed non-repeatable Normal quest must stay refused").IsFalse();
    }

    #region Rig helpers

    /// <summary>
    /// Registers a synthetic quest template in the seeded QuestManager singleton:
    /// _questTemplates for GetTemplate() (used by the reset loop + AddQuest), and a
    /// Start component in _componentTemplates so QuestComponent can resolve it.
    /// </summary>
    private static void RegisterQuest(uint questId, QuestDetail detailId, bool repeatable)
    {
        var questManager = QuestManager.Instance;

        var template = new QuestTemplate
        {
            Id = questId,
            DetailId = detailId,
            Repeatable = repeatable,
            Level = 1
        };

        var componentId = questId * 100 + 1;
        var startComponent = new QuestComponentTemplate(template)
        {
            Id = componentId,
            KindId = QuestComponentKind.Start
        };
        // Never-matching accept act: parks the quest in Start on accept.
        startComponent.ActTemplates.Add(new QuestActConAcceptNpc(startComponent) { NpcId = 999999 });
        template.Components[componentId] = startComponent;

        var componentsField = typeof(QuestManager).GetField("_componentTemplates", BindingFlags.NonPublic | BindingFlags.Instance);
        var registeredComponents = (Dictionary<uint, QuestComponentTemplate>)componentsField!.GetValue(questManager)!;
        registeredComponents[componentId] = startComponent;

        var templatesField = typeof(QuestManager).GetField("_questTemplates", BindingFlags.NonPublic | BindingFlags.Instance);
        var registeredTemplates = (Dictionary<uint, QuestTemplate>)templatesField!.GetValue(questManager)!;
        registeredTemplates[questId] = template;
    }

    private static void SetSingleton(Type singletonBase, object instance)
    {
        var field = singletonBase.GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)
                    ?? throw new InvalidOperationException($"Cannot locate singleton field on {singletonBase.Name}");
        field.SetValue(null, instance);
    }

    #endregion
}
