using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Quests.Static;

using AAEmu.UnitTests.Game.Housing;

using TUnit.Core.Interfaces;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// Workstream E1: the live quest-perception-loop observation projection
/// (<see cref="BotQuestLoopObservation"/>) is what the bridge <c>quest</c> op
/// and the E2E driver both read. These tests pin the projection's OBSERVABLE
/// contract — that it reports exactly what the engine's character state says —
/// without a live server, so a regression in the projection surfaces here
/// rather than as an unexplained E2E failure.
///
/// What is deliberately NOT tested: that the projection forwards fields (that
/// is implementation, not behavior). What IS tested: the values a consumer
/// reads (active rows, ordering, status/step text, arc flags, completion
/// count, ready-set membership) match the character's quest state, and the
/// empty/edge shapes hold.
/// </summary>
[NotInParallel] // touches process-wide singletons (GameplayActorTestRig pattern)
[ParallelLimiter<SequentialParallelLimit>]
public class BotQuestLoopObservationTests
{
    /// <summary>
    /// A character with NO quest work projects an empty, non-throwing snapshot:
    /// zero active rows, zero arc flags set, and an arc that is not "complete".
    /// This is the state every real run starts from (the E2E test's pre-state).
    /// </summary>
    [Test]
    public async Task Capture_NoQuestWork_ProjectsEmptySnapshot()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("quest-obs-empty");
        var character = actor.Character;

        var snapshot = BotQuestLoopObservation.Capture(character, questLegActive: false,
            questIds: [6198u, 330u]);

        await Assert.That(snapshot.CharacterId).IsEqualTo(character.Id);
        await Assert.That(snapshot.ActiveQuests).IsEmpty();
        await Assert.That(snapshot.CompletedCount).IsEqualTo(0);
        await Assert.That(snapshot.ArcCompleted).IsEquivalentTo(new[] { false, false });
        await Assert.That(BotQuestLoopObservation.ArcComplete(snapshot)).IsFalse();
        await Assert.That(BotQuestLoopObservation.ReadyQuestIds(snapshot)).IsEmpty();
    }

    /// <summary>
    /// An ACTIVE quest reads as one active row carrying the engine's own
    /// status/step names and the objective counters, and the arc flag for that
    /// quest stays false while it is merely active (active ≠ completed).
    /// </summary>
    [Test]
    public async Task Capture_ActiveQuest_ProjectsRowAndKeepsArcFlagFalse()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("quest-obs-active");
        var character = actor.Character;
        var quest = new Quest(character)
        {
            TemplateId = 6198u,
            Status = QuestStatus.Progress,
            Step = QuestComponentKind.Progress,
            Objectives = [1, 0, 0, 0, 0],
        };
        character.Quests.ActiveQuests[6198u] = quest;

        var snapshot = BotQuestLoopObservation.Capture(character, questLegActive: true, questIds: [6198u, 330u]);

        await Assert.That(snapshot.ActiveQuests).HasCount().EqualTo(1);
        var row = snapshot.ActiveQuests[0];
        await Assert.That(row.QuestId).IsEqualTo(6198u);
        await Assert.That(row.Status).IsEqualTo(nameof(QuestStatus.Progress));
        await Assert.That(row.Step).IsEqualTo(nameof(QuestComponentKind.Progress));
        await Assert.That(row.Objectives[0]).IsEqualTo(1);
        await Assert.That(snapshot.QuestLegActive).IsTrue();
        await Assert.That(snapshot.ArcCompleted).IsEquivalentTo(new[] { false, false });
    }

    /// <summary>
    /// A quest at Ready is reported in the ready set (the turn-in work
    /// waiting), and only that quest — a Progress quest alongside it must not
    /// appear. This is the gate the E2E turn-in leg reads.
    /// </summary>
    [Test]
    public async Task ReadyQuestIds_ReturnsOnlyReadyQuests()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("quest-obs-ready");
        var character = actor.Character;
        character.Quests.ActiveQuests[6198u] = new Quest(character)
        {
            TemplateId = 6198u,
            Status = QuestStatus.Progress,
            Step = QuestComponentKind.Progress,
        };
        character.Quests.ActiveQuests[330u] = new Quest(character)
        {
            TemplateId = 330u,
            Status = QuestStatus.Ready,
            Step = QuestComponentKind.Ready,
        };

        var snapshot = BotQuestLoopObservation.Capture(character, questLegActive: false, questIds: [6198u, 330u]);
        var ready = BotQuestLoopObservation.ReadyQuestIds(snapshot);

        await Assert.That(ready).IsEquivalentTo(new[] { 330u });
        // Deterministic ordering by id — the projection sorts, so a consumer
        // reading rows never depends on dictionary enumeration order.
        await Assert.That(snapshot.ActiveQuests[0].QuestId).IsEqualTo(330u);
        await Assert.That(snapshot.ActiveQuests[1].QuestId).IsEqualTo(6198u);
    }

    /// <summary>
    /// A COMPLETED quest flips its arc flag, raises the completed count, and is
    /// no longer an active row — the exact transition the E2E chain leg
    /// observes between the first quest and the gated follow-up. With both arc
    /// quests completed the arc reads complete (the E2E pass condition).
    /// </summary>
    [Test]
    public async Task Capture_CompletedArc_FlipsFlagsAndReportsArcComplete()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("quest-obs-complete");
        var character = actor.Character;

        character.Quests.SetCompletedQuestFlag(6198u, true);
        var midArc = BotQuestLoopObservation.Capture(character, questLegActive: false, questIds: [6198u, 330u]);
        await Assert.That(midArc.ArcCompleted).IsEquivalentTo(new[] { true, false });
        await Assert.That(midArc.CompletedCount).IsEqualTo(1);
        await Assert.That(BotQuestLoopObservation.ArcComplete(midArc)).IsFalse();

        character.Quests.SetCompletedQuestFlag(330u, true);
        var fullArc = BotQuestLoopObservation.Capture(character, questLegActive: false, questIds: [6198u, 330u]);
        await Assert.That(fullArc.ArcCompleted).IsEquivalentTo(new[] { true, true });
        await Assert.That(fullArc.CompletedCount).IsEqualTo(2);
        await Assert.That(BotQuestLoopObservation.ArcComplete(fullArc)).IsTrue();
        await Assert.That(fullArc.ActiveQuests).IsEmpty();
    }

    /// <summary>An empty arc list means "no arc to report" — never a vacuous pass.</summary>
    [Test]
    public async Task ArcComplete_EmptyArc_IsFalse()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("quest-obs-noarc");
        var snapshot = BotQuestLoopObservation.Capture(actor.Character, questLegActive: false);

        await Assert.That(snapshot.ArcCompleted).IsEmpty();
        await Assert.That(BotQuestLoopObservation.ArcComplete(snapshot)).IsFalse();
    }

    /// <summary>
    /// The projection is read-only: capturing must not create, complete, or
    /// otherwise mutate quest state (an observer that changed state would make
    /// the E2E evidence self-fulfilling).
    /// </summary>
    [Test]
    public async Task Capture_DoesNotMutateQuestState()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("quest-obs-pure");
        var character = actor.Character;
        character.Quests.ActiveQuests[6198u] = new Quest(character)
        {
            TemplateId = 6198u,
            Status = QuestStatus.Progress,
            Step = QuestComponentKind.Progress,
        };
        var before = character.Quests.ActiveQuests.Count;

        _ = BotQuestLoopObservation.Capture(character, questLegActive: true, questIds: [6198u, 330u]);

        await Assert.That(character.Quests.ActiveQuests.Count).IsEqualTo(before);
        await Assert.That(character.Quests.ActiveQuests.ContainsKey(6198u)).IsTrue();
        await Assert.That(character.Quests.HasQuestCompleted(6198u)).IsFalse();
    }
}
