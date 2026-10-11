namespace AAEmu.IntegrationTests.E2e;

/// <summary>
/// Joins already validated request effects for one productive quest. This adds
/// completion and continuation to the existing purpose proof; it supplies no
/// gameplay and never substitutes chosen fields or counters for request effects.
/// </summary>
internal static class ProductiveQuestEvidence
{
    internal static bool Proved(BotShortRunEvidence proof, uint rootQuest = 251)
    {
        ArgumentNullException.ThrowIfNull(proof);
        return proof.EvidenceFault == null && proof.IdentityMismatches == 0
            && proof.Recoveries.Count == 1
            && Proved(proof.QuestWork, proof.CompletedGoals, proof.PurposeProved,
                proof.Recoveries[0].QuestId, rootQuest);
    }

    // Contract-test seam: callers supply validated Work records, never raw
    // observations. Production enters through the proof overload above.
    internal static bool Proved(IReadOnlyList<BotShortRunEvidence.Work> work,
        IReadOnlyList<BotShortRunEvidence.Work> completedGoals, bool purposeProved,
        uint recoveryQuestId, uint rootQuest = 251)
    {
        if (!purposeProved || rootQuest == 0 || recoveryQuestId != rootQuest
            || work.Any(w => w.TraceId == Guid.Empty || w.CompletedAt < w.RequestedAt)
            || work.Select(w => w.TraceId).Distinct().Count() != work.Count
            || completedGoals.Select(w => w.TraceId).Distinct().Count() != completedGoals.Count)
            return false;

        foreach (var accepted in work.Where(w => w.QuestId == rootQuest && w.Action == "AcceptQuest"))
        {
            foreach (var loot in work.Where(w => w.QuestId == rootQuest && w.Action == "Loot"
                && w.RequestedAt > accepted.CompletedAt))
            {
                foreach (var completion in completedGoals.Where(w => w.QuestId == rootQuest
                    && w.Action is "TurnInQuest" or "TurnInDoodad" or "AutoTurnIn"
                    && w.RequestedAt > loot.CompletedAt && work.Contains(w)))
                {
                    // Require three distinct validated objective/item gains before
                    // engine-confirmed completion. The live acceptance review
                    // also checks the observed counters; this count alone does
                    // not establish that counters never reset between witnesses.
                    if (rootQuest == 251 && work.Count(w => w.QuestId == rootQuest
                        && w.Action == "Loot" && w.RequestedAt > accepted.CompletedAt
                        && w.CompletedAt < completion.RequestedAt) < 3)
                        continue;
                    if (work.Any(w => w.Action == "AcceptQuest" && w.QuestId != 0
                        && w.QuestId != rootQuest && w.RequestedAt > completion.CompletedAt))
                        return true;
                }
            }
        }
        return false;
    }
}
