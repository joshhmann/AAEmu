using AAEmu.Commons.IO;

using NLog;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// Runtime knobs for the pre-LLM bot social layer (ROADMAP M8.5a chatter
/// tiering). All budgets are HARD requirements from the roadmap: a bot never
/// speaks more often than <see cref="PerBotCooldown"/>, a zone never sees more
/// than <see cref="ZoneMessagesPerMinute"/> lines per minute, and nothing is
/// ever sent while the bot or its target is in combat.
///
/// The feature is OFF by default ("Bots"."EnableChatter": true in
/// Config.Local.json, or AAEMU_BOT_CHATTER_ENABLED=1) so prod config stays
/// quiet unless explicitly opted in.
/// </summary>
public sealed record BotChatterOptions
{
    /// <summary>Master gate. Default OFF.</summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// Proximity radius (meters) inside which a nearby character triggers a
    /// greeting from an embodied bot.
    /// </summary>
    public float GreetingRadius { get; init; } = 15f;

    /// <summary>
    /// Minimum quiet time between ANY two lines from the same bot (ROADMAP:
    /// "per-bot cooldown between any two lines", ≥90s).
    /// </summary>
    public TimeSpan PerBotCooldown { get; init; } = TimeSpan.FromSeconds(90);

    /// <summary>
    /// Minimum time before the same bot greets the SAME target again (stops
    /// walk-by loops between bots patrolling the same route).
    /// </summary>
    public TimeSpan PairCooldown { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Per-zone message budget cap per minute window.</summary>
    public int ZoneMessagesPerMinute { get; init; } = 10;

    /// <summary>Proximity scan cadence when subscribed to the game-loop tick.</summary>
    public TimeSpan ScanInterval { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>The inert default: everything off.</summary>
    public static BotChatterOptions Disabled { get; } = new();

    /// <summary>
    /// Reads the runtime gate + overrides: env first (AAEMU_BOT_CHATTER_*),
    /// then Config.Local.json → Config.json "Bots" object. Missing everything
    /// reads as disabled.
    /// </summary>
    public static BotChatterOptions FromEnvironment()
    {
        var enabled = ReadEnabledFlag();
        var options = new BotChatterOptions { Enabled = enabled };

        var envRadius = Environment.GetEnvironmentVariable("AAEMU_BOT_CHATTER_RADIUS");
        if (float.TryParse(envRadius, out var radius) && radius > 0f)
            options = options with { GreetingRadius = radius };

        var envBudget = Environment.GetEnvironmentVariable("AAEMU_BOT_CHATTER_ZONE_BUDGET");
        if (int.TryParse(envBudget, out var budget) && budget > 0)
            options = options with { ZoneMessagesPerMinute = budget };

        return options;
    }

    /// <summary>
    /// True only when chatter is explicitly enabled: AAEMU_BOT_CHATTER_ENABLED=1/true,
    /// or "Bots"."EnableChatter" boolean true in Config.Local.json / Config.json.
    /// </summary>
    public static bool ReadEnabledFlag()
    {
        var env = Environment.GetEnvironmentVariable("AAEMU_BOT_CHATTER_ENABLED");
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
                    bots.TryGetProperty("EnableChatter", out var flag) &&
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
                    "BotChatterOptions: failed to read {Path}", path);
            }
        }

        return false;
    }
}
