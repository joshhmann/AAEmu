using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Quests.Static;

namespace AAEmu.UnitTests.Game.Quests.Scenario;

public enum StageOutcome
{
    NotRun = 0,
    Pass = 1,
    Fail = 2,
    Skip = 3
}

/// <summary>
/// Per-stage verdict record produced by <see cref="QuestScenarioAssertions"/>.
/// </summary>
public class QuestScenarioStageVerdict
{
    public string Stage { get; set; } = "";
    public StageOutcome Outcome { get; set; } = StageOutcome.NotRun;
    /// <summary>Observed engine state at evaluation time (diagnostics).</summary>
    public QuestComponentKind StepObserved { get; set; }
    public QuestStatus StatusObserved { get; set; }
    public string Reason { get; set; } = "";

    public override string ToString() => $"[{Outcome}] {Stage}{(Reason.Length > 0 ? " - " + Reason : "")}";
}

/// <summary>
/// Per-quest verdict record: one entry per stage plus an overall outcome.
/// </summary>
public class QuestScenarioVerdict
{
    public uint QuestId { get; set; }
    public string Name { get; set; } = "";
    public StageOutcome Overall { get; set; } = StageOutcome.NotRun;
    public List<QuestScenarioStageVerdict> Stages { get; } = [];
    /// <summary>Live quest reference after the run (diagnostics; null when skipped/build-failed).</summary>
    public Quest QuestRef { get; set; }

    public override string ToString()
    {
        return $"Quest {QuestId} ({Name}): {Overall}\n  " + string.Join("\n  ", Stages.Select(s => s.ToString()));
    }
}

/// <summary>
/// Verdict builder: turns a stage's executed state (quest, character, persist
/// round-trip results) into a PASS/FAIL/SKIP verdict against the manifest's
/// "expect" block. A stage with no expectations that ran without error is
/// reported as SKIP-with-reason (intentionally not asserted), so tier manifests
/// can mark stages as observational.
/// </summary>
public static class QuestScenarioAssertions
{
    public static QuestScenarioStageVerdict Fail(string stage, string reason)
    {
        return new QuestScenarioStageVerdict { Stage = stage, Outcome = StageOutcome.Fail, Reason = reason };
    }

    /// <summary>
    /// Evaluates one stage's expectations against the executed quest state.
    /// </summary>
    /// <param name="manifest">The scenario manifest being run.</param>
    /// <param name="quest">The driven quest after the stage executed.</param>
    /// <param name="character">The rigged owner character (for inventory + completed flags).</param>
    /// <param name="stage">The stage shape (expectations).</param>
    /// <param name="persistSnapshotData">WriteData() bytes from the last non-terminal stage (PERSIST only).</param>
    /// <param name="persistFreshQuest">Fresh quest that already received ReadData(persistSnapshotData) (PERSIST only).</param>
    /// <param name="persistSnapshot">Decoded snapshot state (step/acceptor/componentId/objectives) captured when the
    /// snapshot was taken. The live quest may have moved on (REWARD drops it), so PERSIST equality is checked
    /// against this captured state, not against the live quest.</param>
    public static QuestScenarioStageVerdict EvaluateStage(
        QuestScenarioManifest manifest,
        Quest quest,
        Character character,
        QuestStageShape stage,
        byte[] persistSnapshotData = null,
        Quest persistFreshQuest = null,
        QuestScenarioDriver.PersistSnapshot persistSnapshot = null)
    {
        var failures = new List<string>();
        var expect = stage.Expect;

        if (!string.IsNullOrEmpty(expect.Step))
        {
            var expected = Enum.Parse<QuestComponentKind>(expect.Step, ignoreCase: true);
            if (quest.Step != expected)
                failures.Add($"expected step {expected}, got {quest.Step}");
        }

        if (!string.IsNullOrEmpty(expect.Status))
        {
            var expected = Enum.Parse<QuestStatus>(expect.Status, ignoreCase: true);
            if (quest.Status != expected)
                failures.Add($"expected status {expected}, got {quest.Status}");
        }

        if (expect.Objectives != null)
        {
            for (var i = 0; i < expect.Objectives.Length && i < quest.Objectives.Length; i++)
            {
                if (quest.Objectives[i] != expect.Objectives[i])
                    failures.Add($"expected objective[{i}] = {expect.Objectives[i]}, got {quest.Objectives[i]}");
            }
        }

        if (expect.RewardItems != null)
        {
            foreach (var rewardItem in expect.RewardItems)
            {
                var found = quest.Owner.Inventory.GetItemsCount(rewardItem.ItemId);
                if (found < rewardItem.Count)
                    failures.Add($"expected reward item {rewardItem.ItemId} x{rewardItem.Count} in inventory, found {found}");
            }
        }

        if (expect.Completed == true && !character.Quests.IsQuestComplete(manifest.QuestId))
            failures.Add("expected completed-quest flag set, found not completed");

        // RC-6 harness fidelity (t_2d482bc3): when the reward path grants ability
        // exp (QuestActSupplyExp with a positive amount), the rigged owner must
        // actually receive it. BuildQuest rigs Ability1..3 to seeded ability keys
        // (Fight..Love); without that rig the abilities default to
        // AbilityType.General (0), which the CharacterAbilities ctor never seeds,
        // and AddActiveExp silently drops the reward - this check surfaces that
        // instead of masking it. Zero-exp supply acts (canonical 1.2 data for
        // 4901/4902/4903/6280) grant nothing by design and are skipped.
        var supplyExpTotal = expect.Completed == true
            ? quest.Template.Components.Values
                .SelectMany(c => c.ActTemplates)
                .OfType<AAEmu.Game.Models.Game.Quests.Acts.QuestActSupplyExp>()
                .Sum(a => a.Exp)
            : 0;
        if (supplyExpTotal > 0)
        {
            var abilityExpGained = 0;
            foreach (var ability in new[] { character.Ability1, character.Ability2, character.Ability3 })
            {
                if (character.Abilities.Abilities.TryGetValue(ability, out var abilityState))
                    abilityExpGained += abilityState.Exp;
            }

            if (abilityExpGained <= 0)
                failures.Add("expected ability exp granted by QuestActSupplyExp reward, found none");
        }

        if (expect.PersistRoundTrip == true)
        {
            if (persistSnapshotData == null)
            {
                failures.Add("no persist snapshot available (PERSIST stage must follow a non-terminal stage)");
            }
            else if (persistFreshQuest == null)
            {
                failures.Add("no round-trip quest available");
            }
            else
            {
                // Strong round-trip: re-serialized state must be byte-identical to the snapshot.
                var roundTripped = persistFreshQuest.WriteData();
                if (!roundTripped.SequenceEqual(persistSnapshotData))
                    failures.Add("WriteData -> ReadData round-trip changed quest state (byte mismatch)");

                // Readable equality checks against the captured snapshot state (the live
                // quest may have been dropped by the REWARD stage by now).
                if (persistSnapshot != null)
                {
                    if (persistFreshQuest.Step != persistSnapshot.Step)
                        failures.Add($"round-trip step mismatch: {persistSnapshot.Step} -> {persistFreshQuest.Step}");
                    if (persistFreshQuest.QuestAcceptorType != persistSnapshot.AcceptorType)
                        failures.Add($"round-trip acceptor type mismatch: {persistSnapshot.AcceptorType} -> {persistFreshQuest.QuestAcceptorType}");
                    if (persistFreshQuest.AcceptorId != persistSnapshot.AcceptorId)
                        failures.Add($"round-trip acceptor id mismatch: {persistSnapshot.AcceptorId} -> {persistFreshQuest.AcceptorId}");
                    if (persistFreshQuest.ComponentId != persistSnapshot.ComponentId)
                        failures.Add($"round-trip component id mismatch: {persistSnapshot.ComponentId} -> {persistFreshQuest.ComponentId}");
                    for (var i = 0; i < persistSnapshot.Objectives.Length; i++)
                    {
                        if (persistFreshQuest.Objectives[i] != persistSnapshot.Objectives[i])
                            failures.Add($"round-trip objective[{i}] mismatch: {persistSnapshot.Objectives[i]} -> {persistFreshQuest.Objectives[i]}");
                    }
                }
            }
        }

        if (expect.FailPathWired == true)
        {
            var hasTimerAct = quest.Template.Components.Values
                .SelectMany(c => c.ActTemplates)
                .Any(a => a is AAEmu.Game.Models.Game.Quests.Acts.QuestActCheckTimer);
            var hasFailComponent = quest.Template.Components.Values
                .Any(c => c.KindId == QuestComponentKind.Fail);
            if (!hasTimerAct && !hasFailComponent)
                failures.Add("expected fail path (QuestActCheckTimer act or Fail component), none wired");
        }

        if (failures.Count > 0)
            return new QuestScenarioStageVerdict { Stage = stage.Name, Outcome = StageOutcome.Fail, Reason = string.Join("; ", failures) };

        if (!expect.HasAnyExpectation)
            return new QuestScenarioStageVerdict { Stage = stage.Name, Outcome = StageOutcome.Skip, Reason = "no expectations defined - stage ran without error" };

        return new QuestScenarioStageVerdict { Stage = stage.Name, Outcome = StageOutcome.Pass };
    }
}
