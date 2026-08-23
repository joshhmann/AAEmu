using AAEmu.Commons.IO;

using NLog;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// Runtime knobs for C1 Schedules v1 (M8/G4). The feature is OFF by default
/// ("Bots"."EnableSchedules": true in Config.Local.json, or
/// AAEMU_BOT_SCHEDULES_ENABLED=1) so prod config and the B4 E2E stay
/// untouched unless explicitly opted in.
///
/// <see cref="HysteresisHours"/> / <see cref="TravelDurationHours"/> are
/// GAME-CLOCK hours (TimeManager scale), not wall-clock.
/// </summary>
public sealed record BotScheduleOptions
{
    /// <summary>Master gate. Default OFF.</summary>
    public bool Enabled { get; init; }

    /// <summary>Phase-resolution cadence when subscribed to the game-loop tick.</summary>
    public TimeSpan ScanInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Anti-flap window around phase boundaries (game hours).</summary>
    public float HysteresisHours { get; init; } = BotScheduleResolver.DefaultHysteresisHours;

    /// <summary>Length of the Travel legs to/from the work anchor (game hours).</summary>
    public float TravelDurationHours { get; init; } = BotScheduleResolver.DefaultTravelDurationHours;

    /// <summary>The inert default: everything off.</summary>
    public static BotScheduleOptions Disabled { get; } = new();

    /// <summary>
    /// Reads the runtime gate + overrides: env first (AAEMU_BOT_SCHEDULES_*),
    /// then Config.Local.json → Config.json "Bots" object. Missing everything
    /// reads as disabled.
    /// </summary>
    public static BotScheduleOptions FromEnvironment()
    {
        var options = new BotScheduleOptions { Enabled = ReadEnabledFlag() };

        var envScan = Environment.GetEnvironmentVariable("AAEMU_BOT_SCHEDULE_SCAN_SECONDS");
        if (int.TryParse(envScan, out var scanSeconds) && scanSeconds > 0)
            options = options with { ScanInterval = TimeSpan.FromSeconds(scanSeconds) };

        return options;
    }

    /// <summary>
    /// True only when schedules are explicitly enabled:
    /// AAEMU_BOT_SCHEDULES_ENABLED=1/true, or "Bots"."EnableSchedules"
    /// boolean true in Config.Local.json / Config.json.
    /// </summary>
    public static bool ReadEnabledFlag()
    {
        var env = Environment.GetEnvironmentVariable("AAEMU_BOT_SCHEDULES_ENABLED");
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
                    bots.TryGetProperty("EnableSchedules", out var flag) &&
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
                    "BotScheduleOptions: failed to read {Path}", path);
            }
        }

        return false;
    }
}
