namespace AAEmu.ArchaeologyMcp;

/// <summary>
/// Optional on-disk cache for table metadata (list of tables + per-table
/// column info) keyed by DB path + file length + last-write time. The cache
/// is a pure performance optimization: it is never required for correctness,
/// and a stale or missing cache entry is simply rebuilt. Cache writes are
/// best-effort and never fail the request.
///
/// Env: ARCHEAGE_CACHE_DIR (optional). When unset, no disk cache is used.
/// </summary>
public sealed class MetadataCache
{
    private readonly string? _dir;

    internal MetadataCache(string? dir) => _dir = dir;

    public static MetadataCache FromEnvironment()
    {
        var dir = Environment.GetEnvironmentVariable("ARCHEAGE_CACHE_DIR");
        return new MetadataCache(string.IsNullOrWhiteSpace(dir) ? null : Path.GetFullPath(dir));
    }

    /// <summary>Reads a cached payload, or null when absent/stale/unreadable.</summary>
    public string? Read(string dbPath, string key)
    {
        if (_dir is null)
            return null;

        try
        {
            var file = CacheFile(dbPath, key);
            if (!File.Exists(file))
                return null;

            var dbInfo = new FileInfo(dbPath);
            var cacheInfo = new FileInfo(file);
            if (!dbInfo.Exists || cacheInfo.LastWriteTimeUtc < dbInfo.LastWriteTimeUtc)
                return null;

            return File.ReadAllText(file);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Writes a payload to the cache (best-effort; never throws).</summary>
    public void Write(string dbPath, string key, string payload)
    {
        if (_dir is null)
            return;

        try
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllText(CacheFile(dbPath, key), payload);
        }
        catch (Exception)
        {
            // Cache is optional — ignore write failures.
        }
    }

    private string CacheFile(string dbPath, string key)
    {
        var dbName = Path.GetFileNameWithoutExtension(dbPath);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(dbPath))))[..16];
        return Path.Combine(_dir!, $"{dbName}-{hash}-{key}.json");
    }
}
