using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace AAEmu.ArchaeologyMcp;

/// <summary>
/// Read-only archaeology service: source catalog, SQLite introspection and
/// parameterized queries, bounded file reads, and allowlisted regex search.
/// Every result carries deterministic provenance metadata (tool, source id,
/// path, version, generated-at, truncation flags).
///
/// Security posture: SQLite connections are always Mode=ReadOnly; SQL is
/// allow-listed by <see cref="SqlGuard"/>; file paths are normalized and
/// must resolve inside an allowlisted root; no shell execution anywhere.
/// </summary>
public sealed partial class ArchaeologyService
{
    public const int DefaultQueryLimit = 100;
    public const int MaxQueryLimit = 1000;
    public const int MaxColumns = 50;
    public const int MaxReadBytes = 1_000_000;      // 1 MiB per read_file
    public const int MaxSearchResults = 500;
    public const int MaxSearchFilesScanned = 10_000;
    public const int QueryTimeoutSeconds = 10;

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private readonly SourceCatalog _catalog;
    private readonly MetadataCache _cache;

    /// <summary>Test seam: overrides the query_sql wall-clock deadline (seconds).</summary>
    internal int? QueryTimeoutSecondsOverride;

    /// <summary>
    /// Thrown by <see cref="EnumerateFiles"/> when the deterministic scan cap
    /// (<see cref="MaxSearchFilesScanned"/>) is reached, so search_files can
    /// report truncation precisely instead of materializing an unbounded
    /// file list.
    /// </summary>
    private sealed class SearchScanCappedException : Exception { }

    public ArchaeologyService(SourceCatalog catalog, MetadataCache cache)
    {
        _catalog = catalog;
        _cache = cache;
    }

    // ------------------------------------------------------------ sources

    public JsonObject ListSources()
    {
        var sources = new JsonArray();
        foreach (var source in _catalog.Sources)
        {
            sources.Add(new JsonObject
            {
                ["source_id"] = source.SourceId,
                ["source_type"] = source.SourceType,
                ["path"] = source.Path,
                ["logical_domain"] = source.LogicalDomain,
                ["version"] = source.Version,
                ["encoding"] = source.Encoding,
                ["size"] = source.Size,
                ["searchable"] = source.Searchable,
                ["notes"] = source.Notes,
            });
        }

        return Result("list_sources", new JsonObject
        {
            ["sources"] = sources,
            ["roots"] = new JsonArray(_catalog.RootIds.Select(r => JsonValue.Create(r)).ToArray()),
        }, sourceId: null, path: null, version: null);
    }

    // ---------------------------------------------------------- databases

    public JsonObject ListDatabases()
    {
        var dbs = new JsonArray();
        foreach (var source in _catalog.Sources.Where(s => s.SourceType == "sqlite"))
        {
            dbs.Add(DbInfo(source));
        }

        // Also surface any *.sqlite3 files inside the data root (e.g. copies).
        if (Directory.Exists(_catalog.DataRoot))
        {
            foreach (var file in Directory.EnumerateFiles(_catalog.DataRoot, "*.sqlite3", SearchOption.TopDirectoryOnly)
                         .OrderBy(f => f, StringComparer.Ordinal))
            {
                if (file == _catalog.DbPath)
                    continue;
                // Symlink guard: a *.sqlite3 symlink escaping the data root
                // must not be surfaced as a database.
                var real = ResolveRealPath(file);
                if (real is null || !_catalog.IsAllowed(real))
                    continue;
                dbs.Add(new JsonObject
                {
                    ["db_id"] = "file:" + Path.GetFileName(file),
                    ["path"] = file,
                    ["version"] = "unknown",
                    ["size"] = new FileInfo(file).Length,
                    ["read_only"] = true,
                });
            }
        }

        return Result("list_databases", new JsonObject { ["databases"] = dbs },
            sourceId: null, path: null, version: null);
    }

    private JsonObject DbInfo(Source source)
    {
        var exists = File.Exists(source.Path);
        return new JsonObject
        {
            ["db_id"] = "compact.sqlite3",
            ["path"] = source.Path,
            ["version"] = source.Version,
            ["size"] = exists ? new FileInfo(source.Path).Length : 0,
            ["read_only"] = true,
            ["exists"] = exists,
        };
    }

    // ------------------------------------------------------------- tables

    public JsonObject ListTables(string dbId)
    {
        var dbPath = ResolveDb(dbId);
        var tables = new JsonArray();
        var cached = _cache.Read(dbPath, "tables");
        if (cached is not null)
        {
            foreach (var name in JsonSerializer.Deserialize<string[]>(cached, JsonOpts) ?? [])
                tables.Add(JsonValue.Create(name));
        }
        else
        {
            using var connection = OpenReadOnly(dbPath);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT name FROM sqlite_master WHERE type IN ('table','view') AND name NOT LIKE 'sqlite_%' ORDER BY name";
            using var reader = command.ExecuteReader();
            while (reader.Read())
                tables.Add(JsonValue.Create(reader.GetString(0)));

            _cache.Write(dbPath, "tables", JsonSerializer.Serialize(
                tables.Select(t => t!.GetValue<string>()).ToArray(), JsonOpts));
        }

        return Result("list_tables", new JsonObject { ["tables"] = tables },
            sourceId: dbId, path: dbPath, version: _catalog.DbVersion);
    }

    public JsonObject DescribeTable(string dbId, string table)
    {
        var dbPath = ResolveDb(dbId);
        if (!IsValidIdentifier(table))
            return ErrorResult("describe_table", $"invalid table name: {table}");

        var columns = new JsonArray();
        var cacheKey = "table-" + table;
        var cached = _cache.Read(dbPath, cacheKey);
        if (cached is not null)
        {
            var cachedColumns = JsonSerializer.Deserialize<JsonArray>(cached, JsonOpts);
            if (cachedColumns is not null)
                columns = cachedColumns;
        }
        else
        {
            using var connection = OpenReadOnly(dbPath);
            using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA table_info({table})";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                columns.Add(new JsonObject
                {
                    ["cid"] = reader.GetInt32(0),
                    ["name"] = reader.GetString(1),
                    ["type"] = reader.IsDBNull(2) ? null : reader.GetString(2),
                    ["notnull"] = reader.GetInt32(3) != 0,
                    ["pk"] = reader.GetInt32(5) != 0,
                });
            }

            _cache.Write(dbPath, cacheKey, columns.ToJsonString());
        }

        return Result("describe_table", new JsonObject
        {
            ["table"] = table,
            ["columns"] = columns,
        }, sourceId: dbId, path: dbPath, version: _catalog.DbVersion);
    }

    // ------------------------------------------------------------ query_sql

    public JsonObject QuerySql(string dbId, string sql, JsonObject? parameters, int? limit)
    {
        var dbPath = ResolveDb(dbId);
        var rejection = SqlGuard.Validate(sql);
        if (rejection is not null)
            return ErrorResult("query_sql", $"SQL rejected: {rejection}");

        var rowLimit = Math.Clamp(limit ?? DefaultQueryLimit, 1, MaxQueryLimit);
        var started = DateTimeOffset.UtcNow;
        var timeoutSeconds = QueryTimeoutSecondsOverride ?? QueryTimeoutSeconds;

        try
        {
            using var connection = OpenReadOnly(dbPath);
            // Microsoft.Data.Sqlite ignores CommandTimeout for SQLite, so the
            // wall-clock deadline is enforced natively: sqlite3_progress_handler
            // invokes the callback every 1000 VM instructions; the callback
            // returns 1 (interrupt) once the deadline has passed. The handler
            // is cleared in finally. The delegate is a per-query local (closure
            // over the local deadline) so overlapping requests never share
            // mutable query state; the local roots the delegate for the
            // connection's lifetime.
            var deadline = started.AddSeconds(timeoutSeconds);
            var progress = new delegate_progress(_ => DateTimeOffset.UtcNow >= deadline ? 1 : 0);
            raw.sqlite3_progress_handler(connection.Handle, 1000, progress, null);
            try
            {
                using var command = connection.CreateCommand();
                command.CommandText = sql;
                BindParameters(command, parameters);

                using var reader = command.ExecuteReader();
                var columnCount = reader.FieldCount;
                if (columnCount > MaxColumns)
                    return ErrorResult("query_sql", $"query returns {columnCount} columns; maximum is {MaxColumns}");

                var columnNames = new string[columnCount];
                for (var i = 0; i < columnCount; i++)
                    columnNames[i] = reader.GetName(i);

                var rows = new JsonArray();
                var truncated = false;
                while (reader.Read())
                {
                    if (rows.Count >= rowLimit)
                    {
                        truncated = true;
                        break;
                    }

                    var row = new JsonObject();
                    for (var i = 0; i < columnCount; i++)
                        row[columnNames[i]] = ReadValue(reader, i);
                    rows.Add(row);
                }

                var elapsedMs = (long)(DateTimeOffset.UtcNow - started).TotalMilliseconds;
                return Result("query_sql", new JsonObject
                {
                    ["columns"] = new JsonArray(columnNames.Select(c => JsonValue.Create(c)).ToArray()),
                    ["rows"] = rows,
                    ["row_count"] = rows.Count,
                    ["truncated"] = truncated,
                    ["limit"] = rowLimit,
                    ["elapsed_ms"] = elapsedMs,
                }, sourceId: dbId, path: dbPath, version: _catalog.DbVersion, truncated: truncated);
            }
            finally
            {
                raw.sqlite3_progress_handler(connection.Handle, 0, null, null);
            }
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 9)
        {
            // SQLITE_INTERRUPT — raised by the progress-handler deadline.
            return ErrorResult("query_sql", $"query timed out after {timeoutSeconds}s");
        }
        catch (SqliteException ex)
        {
            return ErrorResult("query_sql", $"SQLite error: {ex.Message}");
        }
    }

    private static void BindParameters(SqliteCommand command, JsonObject? parameters)
    {
        if (parameters is null)
            return;

        foreach (var (name, value) in parameters)
        {
            var paramName = name.StartsWith('@') || name.StartsWith('$') || name.StartsWith(':')
                ? name
                : "@" + name;
            command.Parameters.AddWithValue(paramName, ToSqliteValue(value));
        }
    }

    private static object? ToSqliteValue(JsonNode? node)
        => node switch
        {
            null => DBNull.Value,
            JsonValue v when v.TryGetValue<long>(out var l) => l,
            JsonValue v when v.TryGetValue<double>(out var d) => d,
            JsonValue v when v.TryGetValue<bool>(out var b) => b ? 1 : 0,
            JsonValue v when v.TryGetValue<string>(out var s) => s,
            _ => node!.ToJsonString(),
        };

    private static JsonValue? ReadValue(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
            return null;

        return reader.GetFieldType(ordinal) switch
        {
            var t when t == typeof(long) => JsonValue.Create(reader.GetInt64(ordinal)),
            var t when t == typeof(double) => JsonValue.Create(reader.GetDouble(ordinal)),
            var t when t == typeof(string) => JsonValue.Create(reader.GetString(ordinal)),
            var t when t == typeof(byte[]) => JsonValue.Create(Convert.ToBase64String((byte[])reader.GetValue(ordinal))),
            _ => JsonValue.Create(reader.GetValue(ordinal)?.ToString()),
        };
    }

    // ------------------------------------------------------------ read_file

    public JsonObject ReadFile(string path, int? offset, int? limit)
    {
        var fullPath = ResolveAllowedPath(path);
        if (fullPath is null)
            return ErrorResult("read_file", $"path not allowed or not found: {path}");

        try
        {
            var info = new FileInfo(fullPath);
            if (!info.Exists)
                return ErrorResult("read_file", $"file not found: {path}");
            if (info.Length > MaxReadBytes)
                return ErrorResult("read_file", $"file too large ({info.Length} bytes; maximum {MaxReadBytes})");

            var start = Math.Max(0, offset ?? 0);
            if (start >= info.Length)
            {
                // Offset at/after EOF: deterministic empty result, never a
                // negative allocation or an exception.
                return Result("read_file", new JsonObject
                {
                    ["path"] = fullPath,
                    ["size"] = info.Length,
                    ["offset"] = start,
                    ["bytes_read"] = 0,
                    ["truncated"] = false,
                    ["content"] = string.Empty,
                }, sourceId: null, path: fullPath, version: null, truncated: false);
            }

            var count = Math.Clamp(limit ?? MaxReadBytes, 1, MaxReadBytes);
            count = (int)Math.Min(count, info.Length - start);

            using var stream = File.OpenRead(fullPath);
            stream.Seek(start, SeekOrigin.Begin);
            var buffer = new byte[count];
            var read = stream.Read(buffer, 0, count);
            var text = Encoding.UTF8.GetString(buffer, 0, read);

            var truncated = start + read < info.Length;
            return Result("read_file", new JsonObject
            {
                ["path"] = fullPath,
                ["size"] = info.Length,
                ["offset"] = start,
                ["bytes_read"] = read,
                ["truncated"] = truncated,
                ["content"] = text,
            }, sourceId: null, path: fullPath, version: null, truncated: truncated);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ErrorResult("read_file", $"read failed: {ex.Message}");
        }
    }

    // --------------------------------------------------------- search_files

    public JsonObject SearchFiles(string pattern, string? rootId, string? glob, int? limit)
    {
        Regex regex;
        try
        {
            regex = new Regex(pattern, RegexOptions.Compiled | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));
        }
        catch (ArgumentException ex)
        {
            return ErrorResult("search_files", $"invalid regex: {ex.Message}");
        }

        var root = rootId is null ? _catalog.DataRoot : _catalog.ResolveRoot(rootId);
        if (root is null || !Directory.Exists(root))
            return ErrorResult("search_files", $"unknown or missing root: {rootId ?? "(default)"}");

        // A glob must stay inside the root: reject absolute/rooted patterns,
        // drive/separator escapes, and any ".." path segment. Directory
        // enumeration would otherwise walk outside the allowlisted root.
        if (!IsSafeGlob(glob))
            return ErrorResult("search_files", $"unsafe glob: {glob}");

        var resultLimit = Math.Clamp(limit ?? MaxSearchResults, 1, MaxSearchResults);
        var matches = new JsonArray();
        var truncated = false;
        var filesScanned = 0;

        try
        {
            foreach (var file in EnumerateFiles(root, glob))
            {
                filesScanned++;
                if (matches.Count >= resultLimit)
                {
                    truncated = true;
                    break;
                }

                // Lexical allow-list plus real-path guard: a symlinked
                // subdirectory escaping the root must not be searched.
                if (!_catalog.IsAllowed(file))
                    continue;
                var real = ResolveRealPath(file);
                if (real is null || !_catalog.IsAllowed(real))
                    continue;
                // Build output (bin/obj) must never be exposed by search.
                if (IsBuildOutputPath(file))
                    continue;

                try
                {
                    var info = new FileInfo(file);
                    if (info.Length > MaxReadBytes)
                        continue; // skip oversized files in search

                    var lineNumber = 0;
                    using var reader = new StreamReader(file, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                    while (reader.ReadLine() is { } line)
                    {
                        lineNumber++;
                        if (regex.IsMatch(line))
                        {
                            matches.Add(new JsonObject
                            {
                                ["path"] = file,
                                ["line"] = lineNumber,
                                ["text"] = line.Length > 200 ? line[..200] + "…" : line,
                            });
                            if (matches.Count >= resultLimit)
                            {
                                truncated = true;
                                break;
                            }
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException)
                {
                    // Skip unreadable/binary files; search is best-effort.
                }
            }
        }
        catch (RegexMatchTimeoutException)
        {
            // Catastrophic backtracking: deterministic failure, never a hang.
            return ErrorResult("search_files", "regex timeout");
        }
        catch (SearchScanCappedException)
        {
            // Deterministic scan cap reached: report truncation, never an
            // unbounded file list.
            truncated = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ErrorResult("search_files", $"search failed: {ex.Message}");
        }

        return Result("search_files", new JsonObject
        {
            ["pattern"] = pattern,
            ["root"] = root,
            ["matches"] = matches,
            ["match_count"] = matches.Count,
            ["files_scanned"] = filesScanned,
            ["truncated"] = truncated,
            ["limit"] = resultLimit,
        }, sourceId: rootId, path: root, version: null, truncated: truncated);
    }

    private static IEnumerable<string> EnumerateFiles(string root, string? glob)
    {
        var pattern = string.IsNullOrWhiteSpace(glob) ? "*" : glob;
        var pending = new Stack<string>();
        pending.Push(root);
        var yielded = 0;

        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            IEnumerable<string> subdirs;
            try
            {
                subdirs = Directory.EnumerateDirectories(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                subdirs = [];
            }

            foreach (var subdir in subdirs.OrderBy(d => d, StringComparer.Ordinal))
            {
                try
                {
                    var attributes = File.GetAttributes(subdir);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                        continue; // symlink/junction — never follow
                    var real = ResolveRealPath(subdir);
                    if (real is null || !IsWithinRoot(root, real))
                        continue; // escaping directory — skip
                    pending.Push(subdir);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Entry vanished or unreadable — skip.
                }
            }

            IEnumerable<string> dirFiles;
            try
            {
                dirFiles = Directory.EnumerateFiles(dir, pattern, SearchOption.TopDirectoryOnly);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue; // unreadable directory — skip
            }

            foreach (var file in dirFiles.OrderBy(f => f, StringComparer.Ordinal))
            {
                if (IsBuildOutputPath(file))
                    continue;
                if (yielded >= MaxSearchFilesScanned)
                    throw new SearchScanCappedException(); // deterministic scan cap — never unbounded
                yielded++;
                yield return file;
            }
        }
    }

    /// <summary>
    /// True when a glob can only match paths inside the enumeration root:
    /// not rooted, no drive/separator escapes, no ".." path segment.
    /// </summary>
    private static bool IsSafeGlob(string? glob)
    {
        if (string.IsNullOrWhiteSpace(glob))
            return true;

        if (Path.IsPathRooted(glob))
            return false;
        if (glob.Contains('\\'))
            return false; // backslash is a separator escape on Windows
        if (glob.IndexOf(':') >= 0)
            return false; // drive/URI escape

        var segments = glob.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.All(s => s != "..");
    }

    /// <summary>True when a path lies under a build-output (bin/obj) directory.</summary>
    private static bool IsBuildOutputPath(string path)
    {
        var segments = path.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (segments[i] is "bin" or "obj")
                return true;
        }

        return false;
    }

    // ------------------------------------------------------------- helpers

    private static bool IsValidIdentifier(string name)
        => Regex.IsMatch(name, @"^[A-Za-z_][A-Za-z0-9_]*$");

    private string ResolveDb(string dbId)
    {
        if (dbId == "compact.sqlite3")
            return _catalog.DbPath;

        if (dbId.StartsWith("file:", StringComparison.Ordinal))
        {
            // The name must be a bare file name (no separators, no traversal,
            // no absolute path) so it can only resolve inside the data root.
            var name = dbId["file:".Length..];
            if (name.Length == 0
                || name.Contains('/') || name.Contains('\\')
                || name == "." || name == ".."
                || Path.IsPathRooted(name))
            {
                throw new ArgumentException($"invalid database id: {dbId}");
            }

            var candidate = Path.Combine(_catalog.DataRoot, name);
            if (File.Exists(candidate) && _catalog.IsAllowed(candidate))
            {
                // Symlink guard: the file must resolve inside the data root.
                var real = ResolveRealPath(candidate);
                if (real is not null && _catalog.IsAllowed(real))
                    return candidate;
            }
        }

        throw new ArgumentException($"unknown database id: {dbId}");
    }

    /// <summary>
    /// Normalizes a user-supplied path and returns the absolute path only
    /// when it exists and resolves inside an allowlisted root. Rejects
    /// traversal (..), symlinks escaping the root (including symlinked
    /// parent directories), and absolute paths outside the allow-list.
    /// </summary>
    private string? ResolveAllowedPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        string full;
        try
        {
            full = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(_catalog.RepoRoot, path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        if (!_catalog.IsAllowed(full))
            return null;

        // Symlink guard: resolve every path component (parents included) and
        // require the fully-resolved real path to stay inside an allowed root.
        var real = ResolveRealPath(full);
        if (real is null || !_catalog.IsAllowed(real))
            return null;

        return File.Exists(full) ? full : null;
    }

    /// <summary>
    /// Resolves all symlinks in a path (each component, parents included) and
    /// returns the canonical real path, or null when a component cannot be
    /// resolved. The final component need not exist (callers check existence).
    /// </summary>
    private static string? ResolveRealPath(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetPathRoot(full);
            if (root is null)
                return null;

            var current = root;
            var rest = full[root.Length..];
            foreach (var part in rest.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, part);
                var info = new FileInfo(current);
                var target = info.ResolveLinkTarget(returnFinalTarget: true);
                if (target is not null)
                    current = target.FullName;
            }

            return Path.GetFullPath(current);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static SqliteConnection OpenReadOnly(string dbPath)
    {
        var connection = new SqliteConnection($"Data Source=file:{dbPath};Mode=ReadOnly");
        connection.Open();
        return connection;
    }

    // ------------------------------------------------------------- framing

    private static JsonObject Result(string tool, JsonObject data, string? sourceId, string? path, string? version, bool truncated = false)
    {
        var result = new JsonObject
        {
            ["ok"] = true,
            ["data"] = data,
            ["provenance"] = new JsonObject
            {
                ["tool"] = tool,
                ["source_id"] = sourceId,
                ["path"] = path,
                ["version"] = version,
                ["generated_at"] = DateTimeOffset.UtcNow.ToString("O"),
                ["truncated"] = truncated,
            },
        };
        return result;
    }

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
