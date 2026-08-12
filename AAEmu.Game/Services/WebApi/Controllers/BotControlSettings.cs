using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AAEmu.Commons.IO;

namespace AAEmu.Game.Services.WebApi.Controllers;

/// <summary>
/// Gate + shared secret for the bot control API (P1 t_2ea94a20). Mirrors the
/// AAEMU_PRESENCE_DEMO posture exactly: DISABLED unless explicitly enabled
/// (AAEMU_BOT_CTRL=1/true env, or Config "Bots"."EnableBotControl"), so prod
/// never exposes the surface by accident. The token is an env secret
/// (AAEMU_BOT_CTRL_TOKEN) with a config fallback (Bots.BotControlToken) —
/// never logged, never shipped in shared configs.
/// </summary>
public static class BotControlSettings
{
    private const string EnvEnabledFlag = "AAEMU_BOT_CTRL";
    private const string EnvToken = "AAEMU_BOT_CTRL_TOKEN";

    /// <summary>True only when the API is explicitly enabled (env or runtime config).</summary>
    public static bool IsEnabled()
        => IsEnabled(Environment.GetEnvironmentVariable(EnvEnabledFlag), ReadConfigs());

    /// <summary>The configured shared secret, or null when not configured.</summary>
    public static string? GetToken()
        => GetToken(Environment.GetEnvironmentVariable(EnvToken), ReadConfigs());

    /// <summary>
    /// Fixed-time comparison against the configured token. Fail-closed: no
    /// configured token (or empty input) never matches.
    /// </summary>
    public static bool TokenMatches(string provided)
        => TokenMatches(provided, Environment.GetEnvironmentVariable(EnvToken), ReadConfigs());

    internal static bool TokenMatches(string provided, string? envToken, IEnumerable<(string Name, string? Json)> configs)
    {
        var expected = GetToken(envToken, configs);
        if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(provided))
            return false;
        var a = Encoding.UTF8.GetBytes(expected);
        var b = Encoding.UTF8.GetBytes(provided);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    // -- Test seams: pure decisions over injected env + config contents ------

    internal static bool IsEnabled(string? envFlag, IEnumerable<(string Name, string? Json)> configs)
    {
        if (envFlag is "1" or "true" or "True")
            return true;

        foreach (var (_, json) in configs)
        {
            if (json is null)
                continue;
            if (TryReadBotsSection(json) is { } bots &&
                bots.TryGetProperty("EnableBotControl", out var flag) &&
                flag.ValueKind == JsonValueKind.True)
                return true;
        }

        return false;
    }

    internal static string? GetToken(string? envToken, IEnumerable<(string Name, string? Json)> configs)
    {
        if (!string.IsNullOrWhiteSpace(envToken))
            return envToken;

        foreach (var (_, json) in configs)
        {
            if (json is null)
                continue;
            if (TryReadBotsSection(json) is { } bots &&
                bots.TryGetProperty("BotControlToken", out var tok) &&
                tok.ValueKind == JsonValueKind.String)
                return tok.GetString();
        }

        return null;
    }

    private static JsonElement? TryReadBotsSection(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("Bots", out var bots) &&
                bots.ValueKind == JsonValueKind.Object)
                return bots.Clone();
        }
        catch (JsonException)
        {
            // malformed config = treated as absent (mirrors the presence gate)
        }

        return null;
    }

    private static IEnumerable<(string Name, string? Json)> ReadConfigs()
    {
        foreach (var fileName in new[] { "Config.Local.json", "Config.json" })
        {
            var path = Path.Combine(FileManager.AppPath, fileName);
            if (!File.Exists(path))
                continue;
            yield return (path, TryReadFile(path));
        }
    }

    private static string? TryReadFile(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception)
        {
            // unreadable config = treated as absent
            return null;
        }
    }
}
