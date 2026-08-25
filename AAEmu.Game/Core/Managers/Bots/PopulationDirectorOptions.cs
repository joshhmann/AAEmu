using AAEmu.Commons.IO;

using NLog;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// Configuration for <see cref="PopulationDirector"/> (spec §11/§14/§15).
///
/// Pressure thresholds are "at or above this value → the band triggers".
/// Density caps are per-zone and per-activity embodied-bot limits; absent
/// keys mean "no cap" for that zone/activity.
/// </summary>
public sealed class PopulationDirectorOptions
{
    // -- Pressure bands (spec §14). Sample values ≥ threshold escalate the band. --
    /// <summary>Worker utilization at or above which the band becomes Pressure. Default 0.50.</summary>
    public double PressureUtilization { get; init; } = 0.50d;

    /// <summary>Worker utilization at or above which the band becomes High. Default 0.75.</summary>
    public double HighUtilization { get; init; } = 0.75d;

    /// <summary>Worker utilization at or above which the band becomes Critical. Default 0.90.</summary>
    public double CriticalUtilization { get; init; } = 0.90d;

    /// <summary>Due-queue depth at or above which the band becomes Pressure. Default 100.</summary>
    public int PressureQueueDepth { get; init; } = 100;

    /// <summary>Due-queue depth at or above which the band becomes High. Default 250.</summary>
    public int HighQueueDepth { get; init; } = 250;

    /// <summary>Due-queue depth at or above which the band becomes Critical. Default 500.</summary>
    public int CriticalQueueDepth { get; init; } = 500;

    /// <summary>Average wake→start latency (ms) at or above which the band becomes Pressure. Default 1000.</summary>
    public double PressureLatencyMs { get; init; } = 1000d;

    /// <summary>Average wake→start latency (ms) at or above which the band becomes High. Default 2500.</summary>
    public double HighLatencyMs { get; init; } = 2500d;

    /// <summary>Average wake→start latency (ms) at or above which the band becomes Critical. Default 5000.</summary>
    public double CriticalLatencyMs { get; init; } = 5000d;

    /// <summary>TickManager p95 (ms) at or above which the band becomes High (probe lands with H2). Default 100.</summary>
    public double HighTickDurationMs { get; init; } = 100d;

    /// <summary>TickManager p95 (ms) at or above which the band becomes Critical (probe lands with H2). Default 250.</summary>
    public double CriticalTickDurationMs { get; init; } = 250d;

    /// <summary>ActiveRegionTick duration (ms) at or above which the band becomes High (probe lands with H2). Default 100.</summary>
    public double HighRegionTickMs { get; init; } = 100d;

    /// <summary>ActiveRegionTick duration (ms) at or above which the band becomes Critical (probe lands with H2). Default 250.</summary>
    public double CriticalRegionTickMs { get; init; } = 250d;

    // -- Pressure policy (what the director DOES with the band) --
    /// <summary>Bands at or above this refuse Dormant→Reduced (new wakes). Default High.</summary>
    public ServerPressure RefuseWakeAtOrAbove { get; init; } = ServerPressure.High;

    /// <summary>Bands at or above this refuse Reduced→Full (escalation). Default Pressure.</summary>
    public ServerPressure RefuseEscalationAtOrAbove { get; init; } = ServerPressure.Pressure;

    /// <summary>Bands at or above this the pressure sweep demotes Full→Reduced. Default High.</summary>
    public ServerPressure DemoteFullAtOrAbove { get; init; } = ServerPressure.High;

    /// <summary>Bands at or above this the pressure sweep demotes Reduced→Dormant. Default Critical.</summary>
    public ServerPressure DemoteReducedAtOrAbove { get; init; } = ServerPressure.Critical;

    // -- Density caps (spec §15 / review deliverable 8) --
    /// <summary>Max embodied bots per zone (absent key = uncapped). Keyed by zone id.</summary>
    public Dictionary<uint, int> ZoneDensityCaps { get; init; } = [];

    /// <summary>Max embodied bots per activity (absent key = uncapped). Keyed by activity id.</summary>
    public Dictionary<string, int> ActivityDensityCaps { get; init; } = [];

    /// <summary>Default zone cap used when a zone has no explicit entry. -1 = uncapped. Default -1.</summary>
    public int DefaultZoneCap { get; init; } = -1;

    /// <summary>Default activity cap used when an activity has no explicit entry. -1 = uncapped. Default -1.</summary>
    public int DefaultActivityCap { get; init; } = -1;

    // -- Proximity fidelity tiers (G2-A3) --
    /// <summary>
    /// Master gate for the proximity fidelity driver. When off (the default),
    /// <see cref="PopulationDirector"/> behaves exactly as before: no sweeps,
    /// no tick subscription — only explicit TrySetFidelity/Wake/Sleep calls
    /// (presence coordinator, admin) assign fidelity.
    /// </summary>
    public bool EnableProximityFidelity { get; init; }

    /// <summary>Humans at or within this distance (meters) target Full fidelity. Default 75.</summary>
    public float FullProximityRadiusM { get; init; } = 75f;

    /// <summary>Humans beyond Full but at or within this distance (meters) target Reduced. Default 200.</summary>
    public float ReducedProximityRadiusM { get; init; } = 200f;

    /// <summary>Cadence of the proximity sweep when subscribed to the game-loop tick. Default 2000ms.</summary>
    public int ProximitySweepIntervalMs { get; init; } = 2000;

    /// <summary>The inert default: everything off.</summary>
    public static PopulationDirectorOptions Disabled { get; } = new();

    /// <summary>
    /// True only when proximity fidelity is explicitly enabled:
    /// AAEMU_BOT_PROXIMITY_FIDELITY=1/true, or "Bots"."EnableProximityFidelity"
    /// boolean true in Config.Local.json / Config.json.
    /// </summary>
    public static bool ReadProximityEnabledFlag()
    {
        var env = Environment.GetEnvironmentVariable("AAEMU_BOT_PROXIMITY_FIDELITY");
        if (env is "1" or "true" or "True")
            return true;

        foreach (var fileName in new[] { "Config.Local.json", "Config.json" })
        {
            var path = Path.Combine(FileManager.AppPath, fileName);
            if (!File.Exists(path))
                continue;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
                if (doc.RootElement.TryGetProperty("Bots", out var bots) &&
                    bots.ValueKind == System.Text.Json.JsonValueKind.Object &&
                    bots.TryGetProperty("EnableProximityFidelity", out var flag) &&
                    (flag.ValueKind == System.Text.Json.JsonValueKind.True ||
                     (flag.ValueKind == System.Text.Json.JsonValueKind.String &&
                      flag.GetString() is "true" or "True" or "1")))
                {
                    return true;
                }
            }
            catch (Exception ex)
            {
                LogManager.GetCurrentClassLogger().Warn(ex,
                    "PopulationDirectorOptions: failed to read {Path}", path);
            }
        }

        return false;
    }

    /// <summary>
    /// Reads the runtime gate + overrides: env first (AAEMU_BOT_PROXIMITY_*),
    /// then Config.Local.json → Config.json "Bots" object via
    /// <see cref="ReadProximityEnabledFlag"/>. Missing everything reads as disabled.
    /// </summary>
    public static PopulationDirectorOptions FromEnvironment()
    {
        float? full = null;
        float? reduced = null;

        var envFull = Environment.GetEnvironmentVariable("AAEMU_BOT_PROXIMITY_FULL_M");
        if (float.TryParse(envFull, out var fullParsed) && fullParsed > 0f)
            full = fullParsed;

        var envReduced = Environment.GetEnvironmentVariable("AAEMU_BOT_PROXIMITY_REDUCED_M");
        if (float.TryParse(envReduced, out var reducedParsed) && reducedParsed > 0f)
            reduced = reducedParsed;

        return new PopulationDirectorOptions
        {
            EnableProximityFidelity = ReadProximityEnabledFlag(),
            FullProximityRadiusM = full ?? 75f,
            ReducedProximityRadiusM = reduced ?? 200f,
        };
    }
}
