using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Quests.Acts;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.Game.Models.Game.Quests.Templates;

namespace AAEmu.UnitTests.Game.Core.Managers;

/// <summary>
/// M1-3: QuestSanityVerifier detection logic. Every defect class the verifier
/// reports must have a test here (fail-before would be a broken verifier; these
/// assert the detector itself fires on each defect shape).
/// </summary>
public class QuestSanityVerifierTests
{
    private sealed record State(
        QuestTemplate Quest,
        QuestComponentTemplate Component,
        Dictionary<uint, QuestTemplate> Quests,
        Dictionary<uint, QuestComponentTemplate> Components,
        Dictionary<uint, QuestActTemplate> BaseActs,
        Dictionary<string, Dictionary<uint, QuestActTemplate>> ByType);

    private static State BuildCleanState()
    {
        var quest = new QuestTemplate { Id = 1 };
        var component = new QuestComponentTemplate(quest) { Id = 100, KindId = QuestComponentKind.Start };
        quest.Components[100] = component;

        var instance = new QuestActObjTalk(component) { DetailId = 500 };
        component.ActTemplates.Add(instance);

        var baseAct = new QuestActTemplate(component) { ActId = 900, DetailId = 500, DetailType = nameof(QuestActObjTalk) };

        return new State(
            quest, component,
            new Dictionary<uint, QuestTemplate> { [1] = quest },
            new Dictionary<uint, QuestComponentTemplate> { [100] = component },
            new Dictionary<uint, QuestActTemplate> { [900] = baseAct },
            new Dictionary<string, Dictionary<uint, QuestActTemplate>>
            {
                [nameof(QuestActObjTalk)] = new() { [500] = instance }
            });
    }

    private static QuestSanityVerifier.SanityReport Run(State state, Dictionary<uint, List<uint>> groupItems = null)
    {
        return QuestSanityVerifier.VerifyLoadedState(
            state.Quests, state.Components, state.BaseActs, state.ByType, groupItems ?? []);
    }

    [Test]
    public async Task VerifyLoadedState_CleanState_NoFindings()
    {
        var state = BuildCleanState();
        var report = Run(state);
        await Assert.That(report.Findings.Count == 0).IsTrue();
    }

    [Test]
    public async Task VerifyLoadedState_UnknownActType_ReportsError()
    {
        var state = BuildCleanState();
        state.BaseActs[900].DetailType = "QuestActObjBogus";
        state.ByType.Remove(nameof(QuestActObjTalk));

        var report = Run(state);

        await Assert.That(report.Findings.Any(f => f.Code == "ACT_UNKNOWN_TYPE" && f.Severity == QuestSanityVerifier.Severity.Error)).IsTrue();
    }

    [Test]
    public async Task VerifyLoadedState_UninstantiatedAct_ReportsError()
    {
        var state = BuildCleanState();
        state.ByType[nameof(QuestActObjTalk)] = new(); // type known, no detail row loaded

        var report = Run(state);

        await Assert.That(report.Findings.Any(f => f.Code == "ACT_UNINSTANTIATED" && f.Severity == QuestSanityVerifier.Severity.Error)).IsTrue();
    }

    [Test]
    public async Task VerifyLoadedState_DetachedAct_ReportsError()
    {
        var state = BuildCleanState();
        var other = new QuestComponentTemplate(state.Quest) { Id = 101, KindId = QuestComponentKind.Progress };
        state.Quest.Components[101] = other;
        state.Components[101] = other;
        // The detail row got wired to the OTHER component, while the base act row
        // belongs to the first component — the act is missing from its own component.
        var instance = new QuestActObjTalk(other) { DetailId = 500 };
        other.ActTemplates.Add(instance);
        state.ByType[nameof(QuestActObjTalk)][500] = instance;

        var report = Run(state);

        await Assert.That(report.Findings.Any(f => f.Code == "ACT_DETACHED" && f.Severity == QuestSanityVerifier.Severity.Error)).IsTrue();
    }

    [Test]
    public async Task VerifyLoadedState_QuestWithoutComponents_ReportsWarning()
    {
        var state = BuildCleanState();
        state.Quest.Components.Clear();

        var report = Run(state);

        await Assert.That(report.Findings.Any(f => f.Code == "QUEST_NO_COMPONENTS" && f.Severity == QuestSanityVerifier.Severity.Warn)).IsTrue();
    }

    [Test]
    public async Task VerifyLoadedState_QuestWithoutStartComponent_ReportsWarning()
    {
        var state = BuildCleanState();
        state.Component.KindId = QuestComponentKind.Progress;

        var report = Run(state);

        await Assert.That(report.Findings.Any(f => f.Code == "QUEST_NO_START" && f.Severity == QuestSanityVerifier.Severity.Warn)).IsTrue();
    }

    [Test]
    public async Task VerifyLoadedState_MissingNextComponent_ReportsError()
    {
        var state = BuildCleanState();
        state.Component.NextComponent = 999;

        var report = Run(state);

        await Assert.That(report.Findings.Any(f => f.Code == "COMPONENT_NEXT_MISSING" && f.Severity == QuestSanityVerifier.Severity.Error)).IsTrue();
    }

    [Test]
    public async Task VerifyLoadedState_KnownStubAct_ReportsWarning()
    {
        var state = BuildCleanState();
        var guard = new QuestActCheckGuard(state.Component) { DetailId = 501 };
        state.Component.ActTemplates.Add(guard);

        var report = Run(state);

        await Assert.That(report.Findings.Any(f => f.Code == "ACT_STUB_KNOWN" && f.Severity == QuestSanityVerifier.Severity.Warn)).IsTrue();
    }

    [Test]
    public async Task VerifyLoadedState_CheckCompleteComponentMissingTarget_ReportsError()
    {
        var state = BuildCleanState();
        var act = new QuestActCheckCompleteComponent(state.Component) { DetailId = 502, CompleteComponent = 999 };
        state.Component.ActTemplates.Add(act);

        var report = Run(state);

        await Assert.That(report.Findings.Any(f => f.Code == "ACT_REF_MISSING_COMPONENT" && f.Severity == QuestSanityVerifier.Severity.Error)).IsTrue();
    }

    [Test]
    public async Task VerifyLoadedState_ConAcceptComponentMissingQuest_ReportsError()
    {
        var state = BuildCleanState();
        var act = new QuestActConAcceptComponent(state.Component) { DetailId = 503, QuestContextId = 999 };
        state.Component.ActTemplates.Add(act);

        var report = Run(state);

        await Assert.That(report.Findings.Any(f => f.Code == "ACT_REF_MISSING_QUEST" && f.Severity == QuestSanityVerifier.Severity.Error)).IsTrue();
    }

    [Test]
    public async Task VerifyLoadedState_CompleteQuestMissingQuest_ReportsError()
    {
        var state = BuildCleanState();
        var act = new QuestActObjCompleteQuest(state.Component) { DetailId = 504, QuestId = 999 };
        state.Component.ActTemplates.Add(act);

        var report = Run(state);

        await Assert.That(report.Findings.Any(f => f.Code == "ACT_REF_MISSING_COMPLETE_QUEST" && f.Severity == QuestSanityVerifier.Severity.Error)).IsTrue();
    }

    [Test]
    public async Task VerifyLoadedState_CheckTimerMissingNextComponent_ReportsError()
    {
        var state = BuildCleanState();
        var act = new QuestActCheckTimer(state.Component) { DetailId = 505, NextComponent = 999 };
        state.Component.ActTemplates.Add(act);

        var report = Run(state);

        await Assert.That(report.Findings.Any(f => f.Code == "ACT_NEXT_MISSING" && f.Severity == QuestSanityVerifier.Severity.Error)).IsTrue();
    }

    [Test]
    public async Task VerifyLoadedState_ConAcceptComponentPresent_ReportsWatchInfo()
    {
        var state = BuildCleanState();
        var act = new QuestActConAcceptComponent(state.Component) { DetailId = 506, QuestContextId = 1 };
        state.Component.ActTemplates.Add(act);
        state.ByType[nameof(QuestActConAcceptComponent)] = new() { [506] = act };

        var report = Run(state);

        await Assert.That(report.Findings.Any(f => f.Code == "ACT_WATCH" && f.Severity == QuestSanityVerifier.Severity.Info)).IsTrue();
    }

    [Test]
    public async Task VerifyLoadedState_ItemGroupGatherMissingGroup_ReportsError()
    {
        var state = BuildCleanState();
        var act = new QuestActObjItemGroupGather(state.Component) { DetailId = 507, ItemGroupId = 999 };
        state.Component.ActTemplates.Add(act);
        state.ByType[nameof(QuestActObjItemGroupGather)] = new() { [507] = act };

        var report = Run(state);

        await Assert.That(report.Findings.Any(f => f.Code == "ACT_GROUP_MISSING" && f.Severity == QuestSanityVerifier.Severity.Error)).IsTrue();
    }
}
