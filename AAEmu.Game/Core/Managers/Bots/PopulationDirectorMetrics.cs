namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// Immutable metrics snapshot of <see cref="PopulationDirector"/> (review
/// deliverable 9 item 8). Fidelity counts are point-in-time; transition
/// counters are cumulative and monotonic.
/// </summary>
/// <param name="DormantCount">Bots currently Dormant.</param>
/// <param name="ReducedCount">Bots currently Reduced.</param>
/// <param name="FullCount">Bots currently Full.</param>
/// <param name="Pressure">Last classified pressure band.</param>
/// <param name="TotalTransitionsApplied">Cumulative successful fidelity transitions.</param>
/// <param name="TotalTransitionsRejected">Cumulative rejected transitions (gate/density/pressure).</param>
/// <param name="TotalWakes">Cumulative accepted wake requests.</param>
/// <param name="TotalSleeps">Cumulative accepted sleep requests.</param>
/// <param name="TotalPressureSweeps">Cumulative pressure-policy sweeps executed.</param>
public sealed record PopulationDirectorMetrics(
    int DormantCount,
    int ReducedCount,
    int FullCount,
    ServerPressure Pressure,
    long TotalTransitionsApplied,
    long TotalTransitionsRejected,
    long TotalWakes,
    long TotalSleeps,
    long TotalPressureSweeps)
{
    /// <summary>Embodied bots (Reduced + Full).</summary>
    public int Embodied => ReducedCount + FullCount;
}
