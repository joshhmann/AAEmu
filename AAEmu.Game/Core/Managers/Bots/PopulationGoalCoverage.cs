namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// Population goal-coverage metrics (autonomy Wave E): gameplay-throughput
/// health beyond operational fidelity — who is progressing, who is
/// starving, and whether autonomous work is fairly distributed.
/// Computed from scheduler + arbiter + audit evidence; no new collection
/// pipeline (reads existing counters and per-bot activity memory).
/// </summary>
public sealed record PopulationGoalCoverage(
    int TotalBots,
    int ProgressingBots,
    int IdleBots,
    int StarvingBots,
    IReadOnlyDictionary<string, int> ActivityDistribution,
    double FairnessIndex)
{
    /// <summary>
    /// Jain's fairness index over per-bot completed-action counts
    /// (1 = perfectly fair, →1/n = starved). Empty → 1 (vacuous).
    /// </summary>
    public static double JainFairness(IReadOnlyList<int> completedPerBot)
    {
        if (completedPerBot.Count == 0)
            return 1.0;
        var sum = completedPerBot.Sum(x => (double)x);
        if (sum <= 0)
            return 1.0;
        var sumSquares = completedPerBot.Sum(x => (double)x * x);
        return sum * sum / (completedPerBot.Count * sumSquares);
    }

    /// <summary>Empty population snapshot (vacuous health).</summary>
    public static PopulationGoalCoverage Empty() => new(0, 0, 0, 0, new Dictionary<string, int>(), 1.0);
}
