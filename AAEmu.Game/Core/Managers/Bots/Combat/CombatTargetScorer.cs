#nullable enable

namespace AAEmu.Game.Core.Managers.Bots.Combat;

/// <summary>
/// The combat brain's TARGET RANKER: given the wake's candidate rows and the
/// engagement's incumbent, the target to commit to — best first, with an
/// incumbent hysteresis so a marginally better newcomer cannot flap the
/// commitment every wake.
///
/// INVARIANT — DETERMINISTIC AND PURE. A row's score is a pure function of the
/// row plus the documented bands below and of nothing else: no archetype, mood,
/// need, personality profile, randomness or wall-clock read exists in this
/// signature. Two observers of any temperament handed the same rows and the same
/// incumbent MUST choose the same target.
///
/// Ordering is a TOTAL order — score desc, then ObjId ASC — so the same input
/// set always yields the same winner and ties are meaningful rather than
/// incidental (the same discipline <c>AttentionScorer.Rank</c> applies).
///
/// Hysteresis contract (the reason this class exists rather than a bare
/// <c>OrderBy</c>): a challenger replaces the incumbent only when it beats the
/// incumbent's score by <see cref="ChallengerMargin"/> AND the incumbent's
/// commitment window has elapsed. Before the window elapses the incumbent is
/// kept when it is still a legal row, so a target that briefly dips in score
/// (a level-delta change, a crowd of arrivals) does not cost the engagement.
/// The window is measured on the caller's clock — the brain owns no timer.
/// </summary>
public static class CombatTargetScorer
{
    /// <summary>The base score of an attention-ranked row (see <see cref="AttentionScoreOf"/>).</summary>
    public const int AttentionWeight = 1000;

    /// <summary>Score award per metre of proximity inside the aggro band (closer is better).</summary>
    public const int ProximityWeight = 20;

    /// <summary>Flat score penalty per hostile level above the observer (an over-levelled target is worse).</summary>
    public const int LevelDeltaPenaltyPerLevel = 250;

    /// <summary>Score bonus for an already-wounded target (finish what is started).</summary>
    public const int WoundedBonus = 100;

    /// <summary>Score bonus for a critically wounded target (the kill is one or two hits away).</summary>
    public const int CriticalBonus = 300;

    /// <summary>Flat score bonus for a row the caller established as quest-relevant.</summary>
    public const int QuestRelevantBonus = 500;

    /// <summary>Flat score penalty per metre the target has drifted from its leash anchor.</summary>
    public const int LeashDistancePenalty = 2;

    /// <summary>
    /// How far a challenger must beat the incumbent before it may replace it:
    /// a 25% margin over the incumbent's own score. Applied to the score's
    /// magnitude (<see cref="ChallengerMarginFraction"/>) so the rule is
    /// symmetric for the negative scores an over-levelled row can produce — a
    /// plain ratio would INVERT the comparison below zero and let a worse
    /// challenger displace a better incumbent.
    /// </summary>
    public const float ChallengerMargin = 1.25f;

    /// <summary>The margin as a magnitude fraction of the incumbent's score (see <see cref="ChallengerMargin"/>).</summary>
    public const float ChallengerMarginFraction = 0.25f;

    /// <summary>Commitment window: the incumbent cannot be replaced before this much time has elapsed.</summary>
    public static readonly TimeSpan CommitmentWindow = TimeSpan.FromSeconds(5);

    /// <summary>Attention band quoted from the perception ladder's top rung (a legal attack target).</summary>
    public const int TargetBand = 100;

    /// <summary>Attention band for an alive hostile inside the aggro band (the ladder's incomplete-frame rung).</summary>
    public const int HostileInAggroBand = 40;

    /// <summary>
    /// The attention score of one row, given the two facts the combat layer can
    /// establish about it: whether it is a legal attack target this wake, and
    /// whether it is an alive hostile inside the aggro band. The bands are
    /// quoted from the perception ladder's own rungs so the two rankers agree
    /// about relative precedence, but this ranker derives its score from the
    /// combat layer's OWN candidate flags (the decision paths may not reference
    /// the belief stack).
    /// </summary>
    public static int AttentionScoreOf(bool attackTarget, bool hostileInAggro)
        => attackTarget ? TargetBand : hostileInAggro ? HostileInAggroBand : 0;

    /// <summary>
    /// The score of one row. Deterministic integer arithmetic only — no clock,
    /// no clock-derived drift, so a replay of the same row yields the same
    /// number.
    ///
    /// Terms, in the order they are documented in the increment's design:
    /// attention band (dominant), proximity inside the aggro band, level delta
    /// penalty, wounded/critical bonus, quest relevance, leash-distance penalty.
    /// A negative level delta (an easier target) awards nothing beyond the
    /// absence of the penalty — an under-levelled target is neither rewarded nor
    /// punished, so the ranker never prefers farming trivial mobs.
    /// </summary>
    public static int Score(in CombatCandidate candidate, byte selfLevel, float aggroBandM)
    {
        var score = candidate.AttentionScore * AttentionWeight;

        if (!float.IsNaN(candidate.DistanceM) && aggroBandM > 0f)
        {
            var within = Math.Clamp(aggroBandM - candidate.DistanceM, 0f, aggroBandM);
            score += (int)MathF.Round(within * ProximityWeight);
        }

        var levelDelta = candidate.TargetLevel - selfLevel;
        if (levelDelta > 0)
            score -= levelDelta * LevelDeltaPenaltyPerLevel;

        if (candidate.TargetHpRatio <= CombatDecisionTree.DefaultEmergencyFleeHpPercent)
            score += CriticalBonus;
        else if (candidate.TargetHpRatio < CombatDecisionTree.DefaultDefensiveHealHpPercent)
            score += WoundedBonus;

        if (candidate.QuestRelevant)
            score += QuestRelevantBonus;

        if (candidate.LeashDistanceM > 0f)
            score += (int)MathF.Round(candidate.LeashDistanceM) * -LeashDistancePenalty;

        return score;
    }

    /// <summary>
    /// The score a challenger must strictly exceed to displace an incumbent
    /// scoring <paramref name="incumbentScore"/>: the incumbent's own magnitude
    /// grown by <see cref="ChallengerMarginFraction"/>. Sign-preserving and
    /// total — see <see cref="ChallengerMargin"/> for why a plain ratio is wrong.
    /// </summary>
    public static double DisplacementBar(int incumbentScore)
        => incumbentScore + (Math.Abs((double)incumbentScore) * ChallengerMarginFraction);

    /// <summary>
    /// Picks the row to commit to. Returns false when no row is a legal
    /// candidate at all (the caller then holds or drops the commitment).
    ///
    /// <paramref name="incumbentObjId"/> + <paramref name="incumbentLegal"/>
    /// describe the incumbent: when it is still a legal row and its commitment
    /// window has not elapsed, it is kept UNCONDITIONALLY (the hysteresis arm);
    /// otherwise the ranked winner must beat <see cref="DisplacementBar"/> to
    /// replace it, so a newcomer that is only marginally better does not steal a
    /// live engagement.
    /// </summary>
    public static bool TrySelect(
        IReadOnlyList<CombatCandidate> candidates,
        uint incumbentObjId,
        bool incumbentLegal,
        int incumbentScore,
        double elapsedSinceCommitSeconds,
        byte selfLevel,
        float aggroBandM,
        out CombatCandidate selected,
        out int selectedScore,
        out bool keptIncumbent)
    {
        selected = default;
        selectedScore = 0;
        keptIncumbent = false;

        var found = false;
        var bestScore = 0;
        var best = default(CombatCandidate);
        for (var i = 0; i < candidates.Count; i++)
        {
            var candidate = candidates[i];
            if (!candidate.IsMeasured || candidate.ObjId == 0)
                continue;
            var score = Score(candidate, selfLevel, aggroBandM);
            if (!found || score > bestScore || (score == bestScore && candidate.ObjId < best.ObjId))
            {
                best = candidate;
                bestScore = score;
                found = true;
            }
        }

        if (!found)
            return false;

        // The incumbent arm: keep the live commitment when it is either still
        // inside its window, or the ranked winner does not clear the margin.
        // One lookup, so the two arms cannot drift apart.
        var mayKeepIncumbent = incumbentObjId != 0 && incumbentLegal &&
            (elapsedSinceCommitSeconds < CommitmentWindow.TotalSeconds ||
             best.ObjId == incumbentObjId ||
             bestScore <= DisplacementBar(incumbentScore));

        if (mayKeepIncumbent)
        {
            for (var i = 0; i < candidates.Count; i++)
            {
                if (candidates[i].ObjId != incumbentObjId)
                    continue;
                selected = candidates[i];
                // Re-score so the caller's recorded score always matches the row.
                selectedScore = Score(selected, selfLevel, aggroBandM);
                keptIncumbent = true;
                return true;
            }
        }

        selected = best;
        selectedScore = bestScore;
        return true;
    }
}
