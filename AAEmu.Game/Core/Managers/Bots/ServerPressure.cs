namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// Adaptive server-pressure state (spec §14, ARCHITECTURE_REVIEW deliverable 10
/// slice #9). PopulationDirector classifies the live metrics sample from
/// <see cref="IPressureProbe"/> into one of these bands and applies the
/// configured pressure policy (wake/sleep decisions).
/// </summary>
public enum ServerPressure : byte
{
    /// <summary>Load comfortably under thresholds — normal wake/sleep policy.</summary>
    Healthy = 0,

    /// <summary>Load rising — mild caution (no new Full escalations by default).</summary>
    Pressure = 1,

    /// <summary>Load high — refuse new wakes, demote Full→Reduced (policy-driven).</summary>
    High = 2,

    /// <summary>Load critical — demote Reduced→Dormant, hard wake refusal.</summary>
    Critical = 3
}
