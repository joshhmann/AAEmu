using System.Numerics;

using AAEmu.Commons.IO;
using AAEmu.Game.Models.Game.Char;

using NLog;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// One roster entry of the G2-A6 presence manifest (JSON-driven bot
/// provisioning). Replaces the demo's hardcoded 3-citizen loop when a
/// manifest is configured ("Bots"."PresenceManifest" or env
/// AAEMU_PRESENCE_MANIFEST).
/// </summary>
/// <param name="Name">Character name (provisioned/adopted like any citizen).</param>
/// <param name="Race">Character race.</param>
/// <param name="Gender">Character gender.</param>
/// <param name="Level">Provisioning level.</param>
/// <param name="ClassAbility">Informational class label for now — the
/// provisioning seam (<see cref="HeadlessSession.Provision"/>) does not take
/// an ability yet; parsed, validated and logged until it does.</param>
/// <param name="Home">Optional patrol home override (same precedence as the
/// AAEMU_PRESENCE_HOME_* knob: explicit home wins over persisted metadata,
/// which wins over the template spawn).</param>
/// <param name="HomeZoneId">Zone of the home override (route terrain probes);
/// null → the bot's own transform zone is used, as today.</param>
/// <param name="Personality">Informational persona label carried in logs and
/// available to downstream behavior layers; no behavioral effect yet.</param>
public sealed record PresenceManifestEntry(
    string Name,
    Race Race,
    Gender Gender,
    byte Level,
    string? ClassAbility = null,
    Vector3? Home = null,
    uint? HomeZoneId = null,
    string? Personality = null);

/// <summary>
/// Loads the G2-A6 presence manifest (JSON array of roster entries) with
/// per-entry failure isolation: one malformed entry is logged and SKIPPED,
/// never aborting the rest of the roster. Only a whole-file failure
/// (missing/unreadable/unparseable JSON) fails the load.
///
/// Schema (all property names case-insensitive):
/// <code>
/// [
///   {
///     "name": "Citizen01",
///     "race": "Nuian",
///     "gender": "Male",
///     "level": 5,
///     "classAbility": "Battlerage",
///     "home": { "x": 15578.0, "y": 15382.0, "z": 126.0, "zoneId": 9 },
///     "personality": "chatty"
///   }
/// ]
/// </code>
/// </summary>
public static class PresenceManifestLoader
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    /// <summary>
    /// Loads the manifest at <paramref name="path"/>. Relative paths resolve
    /// against the app directory (the same root Config.Local.json lives in).
    /// Returns false ONLY when the file cannot be read or parsed as a JSON
    /// array; individual bad entries are skipped with a warning.
    /// </summary>
    public static bool TryLoad(string path, out IReadOnlyList<PresenceManifestEntry> entries)
    {
        entries = [];
        if (string.IsNullOrWhiteSpace(path))
            return false;

        var fullPath = Path.IsPathRooted(path) ? path : Path.Combine(FileManager.AppPath, path);
        if (!File.Exists(fullPath))
        {
            Logger.Warn("PresenceManifestLoader: manifest not found at {Path}", fullPath);
            return false;
        }

        System.Text.Json.JsonDocument doc;
        try
        {
            doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(fullPath));
        }
        catch (System.Text.Json.JsonException ex)
        {
            Logger.Error(ex, "PresenceManifestLoader: manifest {Path} is not valid JSON", fullPath);
            return false;
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array)
            {
                Logger.Error("PresenceManifestLoader: manifest {Path} root must be a JSON array", fullPath);
                return false;
            }

            var parsed = new List<PresenceManifestEntry>();
            var index = -1;
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                index++;
                try
                {
                    var entry = ParseEntry(element);
                    if (entry != null)
                        parsed.Add(entry);
                }
                catch (Exception ex)
                {
                    // Per-entry isolation (G2-A6): one bad roster line must
                    // not kill the whole manifest.
                    Logger.Warn(ex, "PresenceManifestLoader: skipping invalid manifest entry [{Index}] in {Path}",
                        index, fullPath);
                }
            }

            entries = parsed;
            return true;
        }
    }

    /// <summary>Parses one roster entry; returns null when it is unusable.</summary>
    private static PresenceManifestEntry? ParseEntry(System.Text.Json.JsonElement element)
    {
        if (element.ValueKind != System.Text.Json.JsonValueKind.Object)
        {
            Logger.Warn("PresenceManifestLoader: entry is not a JSON object — skipped");
            return null;
        }

        var name = GetString(element, "name");
        if (string.IsNullOrWhiteSpace(name))
        {
            Logger.Warn("PresenceManifestLoader: entry without a usable \"name\" — skipped");
            return null;
        }

        var raceText = GetString(element, "race");
        if (!Enum.TryParse<Race>(raceText, ignoreCase: true, out var race) || race == Race.None)
        {
            Logger.Warn("PresenceManifestLoader: entry {Name} has unknown race \"{Race}\" — skipped",
                name, raceText ?? "<missing>");
            return null;
        }

        var genderText = GetString(element, "gender");
        if (!Enum.TryParse<Gender>(genderText, ignoreCase: true, out var gender) || gender == default)
        {
            Logger.Warn("PresenceManifestLoader: entry {Name} has unknown gender \"{Gender}\" — skipped",
                name, genderText ?? "<missing>");
            return null;
        }

        if (!TryGetInt(element, "level", out var level) || level is < 1 or > byte.MaxValue)
        {
            Logger.Warn("PresenceManifestLoader: entry {Name} has missing/out-of-range level — skipped", name);
            return null;
        }

        Vector3? home = null;
        uint? homeZoneId = null;
        if (element.TryGetProperty("home", out var homeEl) && homeEl.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            var hasX = TryGetFloat(homeEl, "x", out var x);
            var hasY = TryGetFloat(homeEl, "y", out var y);
            var hasZ = TryGetFloat(homeEl, "z", out var z);
            if (!(hasX && hasY && hasZ))
            {
                Logger.Warn("PresenceManifestLoader: entry {Name} has a partial home object (x/y/z required) — home ignored",
                    name);
            }
            else
            {
                home = new Vector3(x, y, z);
                if (TryGetUint(homeEl, "zoneId", out var zoneId))
                    homeZoneId = zoneId;
            }
        }

        return new PresenceManifestEntry(
            Name: name.Trim(),
            Race: race,
            Gender: gender,
            Level: (byte)level,
            ClassAbility: GetString(element, "classAbility"),
            Home: home,
            HomeZoneId: homeZoneId,
            Personality: GetString(element, "personality"));
    }

    private static string? GetString(System.Text.Json.JsonElement element, string property)
        => element.TryGetProperty(property, out var value) &&
           value.ValueKind == System.Text.Json.JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool TryGetInt(System.Text.Json.JsonElement element, string property, out int value)
    {
        value = 0;
        return element.TryGetProperty(property, out var v) &&
               v.ValueKind == System.Text.Json.JsonValueKind.Number &&
               v.TryGetInt32(out value);
    }

    private static bool TryGetUint(System.Text.Json.JsonElement element, string property, out uint value)
    {
        value = 0;
        return element.TryGetProperty(property, out var v) &&
               v.ValueKind == System.Text.Json.JsonValueKind.Number &&
               v.TryGetUInt32(out value);
    }

    private static bool TryGetFloat(System.Text.Json.JsonElement element, string property, out float value)
    {
        value = 0f;
        return element.TryGetProperty(property, out var v) &&
               v.ValueKind == System.Text.Json.JsonValueKind.Number &&
               v.TryGetSingle(out value);
    }
}
