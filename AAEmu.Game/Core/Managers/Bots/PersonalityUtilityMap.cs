namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// Personality → utility mapping (autonomy Wave D): free-form personality
/// archetypes map to BOUNDED goal-preference weights. Weights influence
/// choice among legal options only — legality always wins (the selector
/// evaluates hard preconditions first; personality never overrides).
///
/// Weights are small (±20 against priority scales of 10-30) so personality
/// nudges, never dictates. Unknown/empty personalities map to zeros.
/// </summary>
public static class PersonalityUtilityMap
{
    /// <summary>Goal preference weights for one personality.</summary>
    public sealed record GoalWeights(
        int QuestWeight = 0,
        int FarmWeight = 0,
        int CombatWeight = 0,
        int TradeWeight = 0,
        int CraftWeight = 0,
        int ExploreWeight = 0);

    /// <summary>Maps a free-form personality to bounded goal weights.</summary>
    public static GoalWeights For(string? personality)
    {
        var key = (personality ?? "").Trim().ToLowerInvariant();
        return key switch
        {
            "farmer" => new GoalWeights(QuestWeight: 2, FarmWeight: 15, CraftWeight: 5),
            "merchant" => new GoalWeights(QuestWeight: 2, TradeWeight: 15, FarmWeight: 5),
            "guard" => new GoalWeights(CombatWeight: 15, QuestWeight: 8),
            "pirate" => new GoalWeights(CombatWeight: 12, TradeWeight: 8, ExploreWeight: 5),
            "cheerful" => new GoalWeights(QuestWeight: 8, ExploreWeight: 8, FarmWeight: 5),
            "greedy" => new GoalWeights(TradeWeight: 12, CombatWeight: 5, FarmWeight: 5),
            "lawful" => new GoalWeights(QuestWeight: 10, CombatWeight: 5),
            "paranoid" => new GoalWeights(ExploreWeight: -5, CombatWeight: -5, FarmWeight: 8),
            _ => new GoalWeights(),
        };
    }
}
