namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// Source of server-load metrics for <see cref="PopulationDirector"/> (spec §14).
/// A probe returns a <see cref="PressureSample"/>; the director classifies it
/// into a <see cref="ServerPressure"/> band via configurable thresholds and
/// applies the pressure policy.
///
/// Implementation vehicle: review deliverable 9 probes. The scheduler-backed
/// <see cref="SchedulerPressureProbe"/> is the default; TickManager /
/// ActiveRegionTick probes land with H2 and plug in here.
/// </summary>
public interface IPressureProbe
{
    /// <summary>Reads the current load sample. Never throws — a failed read yields an empty sample.</summary>
    PressureSample Sample();
}
