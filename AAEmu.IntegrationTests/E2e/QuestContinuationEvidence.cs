namespace AAEmu.IntegrationTests.E2e;

/// <summary>
/// Evidence-only join for an independently selected supplied-item delivery after
/// the productive quest. These identifiers filter observations, never bot choices.
/// Normal supply and cleanup are additionally checked in the live receipt review.
/// </summary>
internal static class QuestContinuationEvidence
{
    internal static bool Proved(BotShortRunEvidence proof)
    {
        ArgumentNullException.ThrowIfNull(proof);
        return Proved(proof.QuestWork, proof.CompletedGoals, ProductiveQuestEvidence.Proved(proof));
    }

    // Supplied validated records are a contract seam, not gameplay evidence.
    internal static bool Proved(IReadOnlyList<BotShortRunEvidence.Work> work,
        IReadOnlyList<BotShortRunEvidence.Work> completedGoals, bool productiveProved)
    {
        if (!productiveProved || work.Any(w => w.TraceId == Guid.Empty || w.CompletedAt < w.RequestedAt)
            || work.Select(w => w.TraceId).Distinct().Count() != work.Count
            || completedGoals.Select(w => w.TraceId).Distinct().Count() != completedGoals.Count)
            return false;

        foreach (var predecessor in completedGoals.Where(w => w.QuestId == 251
            && w.Action == "TurnInQuest" && work.Contains(w)))
        foreach (var accepted in work.Where(w => w.QuestId == 324 && w.Action == "AcceptQuest"
            && w.RequestedAt > predecessor.CompletedAt))
        foreach (var reported in completedGoals.Where(w => w.QuestId == 324
            && w.Action == "TurnInQuest" && w.RequestedAt > accepted.CompletedAt && work.Contains(w)))
            if (work.Any(w => w.Action == "AcceptQuest" && w.QuestId != 0
                && w.QuestId != 251 && w.QuestId != 324 && w.RequestedAt > reported.CompletedAt))
                return true;
        return false;
    }
}
