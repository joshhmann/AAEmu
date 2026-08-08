namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// Immutable diagnostics snapshot of the player bot registry (slice #5).
/// <c>Registered</c>/<c>Active</c>/<c>Deactivated</c> are current state
/// counts; the <c>Total*</c>/<c>Failed*</c> counters are cumulative and
/// monotonic for the lifetime of the manager.
/// </summary>
/// <param name="Registered">Current registrations not embodied.</param>
/// <param name="Active">Current embodied bots.</param>
/// <param name="Deactivated">Current retained records (torn down).</param>
/// <param name="TotalSpawns">Cumulative successful spawns.</param>
/// <param name="TotalActivations">Cumulative successful activations.</param>
/// <param name="TotalDeactivations">Cumulative successful deactivations.</param>
/// <param name="FailedSpawns">Cumulative rejected spawns (duplicate id, …).</param>
/// <param name="FailedActivations">Cumulative refused/failed activations.</param>
/// <param name="FailedDeactivations">Cumulative refused/failed deactivations.</param>
public sealed record PlayerBotDiagnostics(
    int Registered,
    int Active,
    int Deactivated,
    long TotalSpawns,
    long TotalActivations,
    long TotalDeactivations,
    long FailedSpawns,
    long FailedActivations,
    long FailedDeactivations)
{
    /// <summary>Total registry entries (Registered + Active + Deactivated).</summary>
    public int Total => Registered + Active + Deactivated;
}
