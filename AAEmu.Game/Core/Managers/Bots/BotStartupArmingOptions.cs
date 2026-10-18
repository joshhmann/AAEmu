using System.Text.Json;

using AAEmu.Commons.IO;

using NLog;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// T2 supported arming — the deployment switch that arms the bot host and
/// enrolls a fixed number of bots at startup (layer-audit L1-c: the whole seam
/// was default-OFF with no supported way to turn it on).
///
/// Keys: <c>"Bots"."EnableStartupArming"</c> (env <c>AAEMU_STARTUP_ARMING</c>)
/// turns it on; <c>"Bots"."StartupBotCount"</c> (env
/// <c>AAEMU_STARTUP_BOT_COUNT</c>) sets how many bots are enrolled. Absent
/// means OFF — the shipped configuration arms nothing.
///
/// Precedence differs from the subsystem's older readers on purpose: env wins,
/// then <c>Config.Local.json</c>, then <c>Config.json</c>, and a value PRESENT
/// in the local file decides even when it says false. The older "any true"
/// readers OR the files together, which would make an operator's local
/// <c>false</c> unable to override a base <c>true</c> — a switch that cannot
/// be turned off is not a switch. The count is clamped to
/// [1, MaxPresenceBots] so the switch cannot enroll more bots than the
/// presence bound allows.
/// </summary>
internal static class BotStartupArmingOptions
{
    internal const string EnvEnabled = "AAEMU_STARTUP_ARMING";
    internal const string EnvBotCount = "AAEMU_STARTUP_BOT_COUNT";
    internal const string ConfigEnabled = "EnableStartupArming";
    internal const string ConfigBotCount = "StartupBotCount";

    /// <summary>Roster size used when the switch is on but no count is configured.</summary>
    internal const int DefaultBotCount = 2;

    /// <summary>
    /// Account-name prefix of the roster this switch enrolls. Stable across
    /// boots on purpose (audit L1-e): the managed account
    /// <c>bot_managed_startup_001</c> is the roster index's identity, so a
    /// restart re-enrols the character that account already owns instead of
    /// provisioning another one.
    /// </summary>
    internal const string AccountPrefix = "startup";

    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    /// <summary>
    /// True when the deployment explicitly asked for startup arming. Absent
    /// (the shipped default) is false.
    /// </summary>
    public static bool ReadEnabledFlag()
        => ReadEnabledFlag(FileManager.AppPath);

    /// <summary>Test seam: reads the switch against a given application directory.</summary>
    internal static bool ReadEnabledFlag(string appPath)
    {
        if (TryParseFlag(Environment.GetEnvironmentVariable(EnvEnabled), out var fromEnv))
            return fromEnv;

        if (ReadBotsProperty(appPath, ConfigEnabled) is not { } element)
            return false;

        if (element.ValueKind == JsonValueKind.True)
            return true;
        if (element.ValueKind == JsonValueKind.False)
            return false;
        return element.ValueKind == JsonValueKind.String
            && TryParseFlag(element.GetString(), out var fromConfig)
            && fromConfig;
    }

    /// <summary>
    /// How many bots the switch enrolls, clamped to [1, MaxPresenceBots].
    /// Only consulted when <see cref="ReadEnabledFlag()"/> is true.
    /// </summary>
    public static int ReadBotCount()
        => ReadBotCount(FileManager.AppPath);

    /// <summary>Test seam: reads the count against a given application directory.</summary>
    internal static int ReadBotCount(string appPath)
    {
        var max = BotPresenceCoordinator.ReadMaxPresenceBots();

        if (int.TryParse(Environment.GetEnvironmentVariable(EnvBotCount), out var fromEnv) && fromEnv > 0)
            return BotPresenceCoordinator.ClampBotCount(fromEnv, max);

        if (ReadBotsProperty(appPath, ConfigBotCount) is { } element
            && element.ValueKind == JsonValueKind.Number
            && element.TryGetInt32(out var fromConfig)
            && fromConfig > 0)
        {
            return BotPresenceCoordinator.ClampBotCount(fromConfig, max);
        }

        return BotPresenceCoordinator.ClampBotCount(DefaultBotCount, max);
    }

    /// <summary>
    /// Reads a <c>"Bots"</c> property with local-file-wins precedence: the
    /// first file that HAS the property decides. Null when neither file has it.
    /// <c>Configurations/*.json</c> is not consulted — no bot key has ever been
    /// read from there.
    /// </summary>
    private static JsonElement? ReadBotsProperty(string appPath, string property)
    {
        foreach (var fileName in new[] { "Config.Local.json", "Config.json" })
        {
            var path = Path.Combine(appPath, fileName);
            if (!File.Exists(path))
                continue;

            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                if (document.RootElement.TryGetProperty("Bots", out var bots)
                    && bots.ValueKind == JsonValueKind.Object
                    && bots.TryGetProperty(property, out var value))
                {
                    return value.Clone();
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "BotStartupArming: failed to read {Path}", path);
            }
        }

        return null;
    }

    private static bool TryParseFlag(string? raw, out bool value)
    {
        switch (raw?.Trim())
        {
            case "1" or "true" or "True":
                value = true;
                return true;
            case "0" or "false" or "False":
                value = false;
                return true;
            default:
                value = false;
                return false;
        }
    }
}
