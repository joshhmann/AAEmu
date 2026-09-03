using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AAEmu.Commons.Utils.AAPak;

namespace AAEmu.ArchaeologyMcp;

/// <summary>
/// Read-only AAPak (game_pak) archive service for the archaeology server.
///
/// The archive path is operator-configured via <c>ARCHEAGE_PAK_PATH</c> only —
/// never hardcoded. The archive is always opened with
/// <c>openAsReadOnly: true</c> and never written, created, or mutated. Only
/// the file table (metadata) is read into memory; file contents are never
/// streamed wholesale. Listing is bounded by a regex filter and a result cap;
/// reading is bounded to a single named entry of at most 1 MiB.
///
/// Security posture: entry names are validated to reject absolute paths,
/// backslashes, and <c>..</c> traversal before any lookup; there is no
/// extraction to arbitrary disk and no shell execution. The archive is closed
/// deterministically in a finally block; the service holds no global mutable
/// state.
/// </summary>
public sealed class PakArchiveService
{
    public const int MaxListResults = 5000;
    public const int MaxReadBytes = 1_000_000; // 1 MiB per read_pak_entry
    public const string DefaultVersion = "1.2 r208022";

    private static readonly Regex DriveLetterPattern = new(@"^[A-Za-z]:", RegexOptions.Compiled);

    private readonly string _pakPath;
    private readonly string _version;

    public PakArchiveService(string pakPath, string version)
    {
        _pakPath = pakPath;
        _version = version;
    }

    /// <summary>
    /// Builds the service from the environment. <c>ARCHEAGE_PAK_PATH</c> is
    /// required (when unset the service reports "not configured" for every
    /// operation); <c>ARCHEAGE_PAK_VERSION</c> is an optional provenance label.
    /// </summary>
    public static PakArchiveService FromEnvironment()
    {
        var pakPath = Environment.GetEnvironmentVariable("ARCHEAGE_PAK_PATH") ?? string.Empty;
        var version = Environment.GetEnvironmentVariable("ARCHEAGE_PAK_VERSION") ?? DefaultVersion;
        return new PakArchiveService(pakPath, version);
    }

    public string PakPath => _pakPath;
    public string Version => _version;
    public bool IsConfigured => !string.IsNullOrWhiteSpace(_pakPath);

    // ------------------------------------------------------- list_pak_entries

    /// <summary>
    /// Lists entry metadata (name, size, offset, md5, timestamps) for entries
    /// whose name matches <paramref name="pattern"/>, bounded to
    /// <paramref name="limit"/> results (default <see cref="MaxListResults"/>).
    /// Only the file table is read; no file contents are streamed.
    /// </summary>
    public JsonObject ListEntries(string? pattern, int? limit)
    {
        if (!IsConfigured)
            return ErrorResult("list_pak_entries", "ARCHEAGE_PAK_PATH is not configured");
        if (!File.Exists(_pakPath))
            return ErrorResult("list_pak_entries", $"pak file not found: {_pakPath}");

        Regex regex;
        try
        {
            regex = new Regex(pattern ?? ".*", RegexOptions.Compiled | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));
        }
        catch (ArgumentException ex)
        {
            return ErrorResult("list_pak_entries", $"invalid regex: {ex.Message}");
        }

        var resultLimit = Math.Clamp(limit ?? MaxListResults, 1, MaxListResults);

        try
        {
            return WithPak("list_pak_entries", pak =>
            {
                var entries = new JsonArray();
                var truncated = false;
                foreach (var name in pak.pakFiles.Keys
                             .Where(k => regex.IsMatch(k))
                             .OrderBy(k => k, StringComparer.Ordinal))
                {
                    if (entries.Count >= resultLimit)
                    {
                        truncated = true;
                        break;
                    }

                    entries.Add(EntryJson(pak.pakFiles[name]));
                }

                return Result("list_pak_entries", new JsonObject
                {
                    ["pattern"] = pattern ?? ".*",
                    ["entries"] = entries,
                    ["entry_count"] = entries.Count,
                    ["truncated"] = truncated,
                    ["limit"] = resultLimit,
                }, path: _pakPath, version: _version, truncated: truncated);
            });
        }
        catch (RegexMatchTimeoutException)
        {
            // Catastrophic backtracking: deterministic failure, never a hang.
            return ErrorResult("list_pak_entries", "regex timeout");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ErrorResult("list_pak_entries", $"list failed: {ex.Message}");
        }
    }

    // -------------------------------------------------------- read_pak_entry

    /// <summary>
    /// Reads a single named entry, bounded to at most
    /// <paramref name="maxBytes"/> bytes (default <see cref="MaxReadBytes"/>).
    /// Returns the entry metadata plus the (possibly truncated) content as
    /// base64. Rejects missing entries and invalid (absolute / backslash /
    /// traversal) names.
    /// </summary>
    public JsonObject ReadEntry(string name, int? maxBytes)
    {
        if (!IsConfigured)
            return ErrorResult("read_pak_entry", "ARCHEAGE_PAK_PATH is not configured");
        if (!IsValidEntryName(name))
            return ErrorResult("read_pak_entry", $"invalid entry name: {name}");
        if (!File.Exists(_pakPath))
            return ErrorResult("read_pak_entry", $"pak file not found: {_pakPath}");

        var readLimit = Math.Clamp(maxBytes ?? MaxReadBytes, 1, MaxReadBytes);

        try
        {
            return WithPak("read_pak_entry", pak =>
            {
                if (!pak.GetFileByName(name, out var pfi))
                    return ErrorResult("read_pak_entry", $"entry not found: {name}");

                // A corrupted FAT can carry negative offsets/sizes; reject
                // them deterministically instead of crashing on allocation
                // or stream construction.
                if (pfi.size < 0 || pfi.offset < 0)
                    return ErrorResult("read_pak_entry", $"malformed archive entry: {name}");

                var size = pfi.size;
                var toRead = (int)Math.Min(size, readLimit);
                var truncated = size > readLimit;

                byte[] data;
                using (var stream = pak.ExportFileAsStream(pfi))
                {
                    data = new byte[toRead];
                    var read = stream.Read(data, 0, toRead);
                    if (read < toRead)
                        Array.Resize(ref data, read);
                }

                return Result("read_pak_entry", new JsonObject
                {
                    ["name"] = name,
                    ["size"] = size,
                    ["bytes_read"] = data.Length,
                    ["truncated"] = truncated,
                    ["limit"] = readLimit,
                    ["content_base64"] = Convert.ToBase64String(data),
                    ["md5"] = Convert.ToHexString(pfi.md5),
                    ["offset"] = pfi.offset,
                    ["create_time"] = pfi.createTime,
                    ["modify_time"] = pfi.modifyTime,
                }, path: _pakPath, version: _version, truncated: truncated);
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ErrorResult("read_pak_entry", $"read failed: {ex.Message}");
        }
    }

    // ------------------------------------------------------------- helpers

    /// <summary>
    /// Opens the archive read-only, runs <paramref name="action"/>, and
    /// deterministically closes the archive in a finally block. Returns an
    /// error result when the archive cannot be opened.
    /// </summary>
    private JsonObject WithPak(string tool, Func<AAPak, JsonObject> action)
    {
        var pak = new AAPak(_pakPath, openAsReadOnly: true, createAsNewPak: false);
        try
        {
            if (!pak.isOpen)
                return ErrorResult(tool, $"failed to open pak: {_pakPath}");
            return action(pak);
        }
        finally
        {
            pak.ClosePak();
        }
    }

    private static JsonObject EntryJson(AAPakFileInfo pfi) => new()
    {
        ["name"] = pfi.name,
        ["size"] = pfi.size,
        ["offset"] = pfi.offset,
        ["md5"] = Convert.ToHexString(pfi.md5),
        ["create_time"] = pfi.createTime,
        ["modify_time"] = pfi.modifyTime,
    };

    /// <summary>
    /// Rejects empty names, backslashes, rooted (absolute) paths, drive-letter
    /// prefixes, and any <c>.</c>/<c>..</c>/empty path segment. Entry names are
    /// only ever used as keys into the in-memory file table — never as
    /// filesystem paths — but the validation is enforced defensively.
    /// </summary>
    private static bool IsValidEntryName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;
        if (name.Contains('\\'))
            return false;
        if (Path.IsPathRooted(name))
            return false;
        if (DriveLetterPattern.IsMatch(name))
            return false;

        foreach (var segment in name.Split('/'))
        {
            if (segment.Length == 0 || segment == "." || segment == "..")
                return false;
        }

        return true;
    }

    // ------------------------------------------------------------- framing

    private static JsonObject Result(string tool, JsonObject data, string path, string version, bool truncated)
        => new()
        {
            ["ok"] = true,
            ["data"] = data,
            ["provenance"] = new JsonObject
            {
                ["tool"] = tool,
                ["source_id"] = "game_pak",
                ["path"] = path,
                ["version"] = version,
                ["generated_at"] = DateTimeOffset.UtcNow.ToString("O"),
                ["truncated"] = truncated,
            },
        };

    private static JsonObject ErrorResult(string tool, string message)
        => new()
        {
            ["ok"] = false,
            ["error"] = message,
            ["provenance"] = new JsonObject
            {
                ["tool"] = tool,
                ["generated_at"] = DateTimeOffset.UtcNow.ToString("O"),
            },
        };
}
