namespace AAEmu.ArchaeologyMcp;

/// <summary>
/// One entry in the read-only source catalog. All metadata is deterministic
/// (computed at catalog build from the filesystem + environment), so
/// <c>list_sources</c> output is stable across runs on the same host.
/// </summary>
public sealed record Source(
    string SourceId,
    string SourceType,
    string Path,
    string LogicalDomain,
    string Version,
    string Encoding,
    long Size,
    bool Searchable,
    string Notes);

/// <summary>
/// Builds and owns the allowlisted read-only source catalog.
///
/// Configuration (all optional; defaults are repo-local only — never
/// machine-specific):
///   AAEMU_ROOT             repo root (default: resolved upward from the app base dir)
///   ARCHEAGE_DATA_ROOT     data root (default <AAEMU_ROOT>/AAEmu.Game/Data)
///   ARCHEAGE_DB_PATH       sqlite reference DB (default <data root>/compact.sqlite3)
///   ARCHEAGE_DB_VERSION    version label for the DB source (default "1.2 r208022")
///   ARCHEAGE_PAK_PATH      AAPak (game_pak) archive path (optional; adds a
///                          game_pak catalog entry when set)
///   ARCHEAGE_PAK_VERSION   provenance label for the pak source (default "1.2 r208022")
///   ARCHEAGE_EXTRA_ROOTS   colon-separated extra allowlisted roots (explicit opt-in)
///
/// Excluded by default (never allowlisted unless explicitly added via
/// ARCHEAGE_EXTRA_ROOTS): .client_files, .server_files, .worktrees, E2E/soak
/// roots, MySQL, and any path containing a secret.
/// </summary>
public sealed class SourceCatalog
{
    public const string DefaultDbVersion = "1.2 r208022";
    public const string DefaultPakVersion = "1.2 r208022";

    private static readonly string[] DefaultRootIds =
    [
        "data", "game-source", "sql", "tools", "scripts", "scorecard-explorations",
    ];

    private readonly Dictionary<string, string> _roots; // rootId -> absolute path
    private readonly List<Source> _sources;

    internal SourceCatalog(string repoRoot, string dataRoot, string dbPath, string dbVersion,
        IReadOnlyDictionary<string, string> extraRoots, string? pakPath = null, string? pakVersion = null)
    {
        RepoRoot = repoRoot;
        DataRoot = dataRoot;
        DbPath = dbPath;
        DbVersion = dbVersion;
        PakPath = pakPath;
        PakVersion = pakVersion ?? DefaultPakVersion;

        _roots = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["data"] = dataRoot,
            ["game-source"] = Path.Combine(repoRoot, "AAEmu.Game"),
            ["sql"] = Path.Combine(repoRoot, "SQL"),
            ["tools"] = Path.Combine(repoRoot, "tools"),
            ["scripts"] = Path.Combine(repoRoot, "Scripts"),
            ["scorecard-explorations"] = Path.Combine(repoRoot, "scorecard-explorations"),
        };
        foreach (var (id, path) in extraRoots)
            _roots[id] = path;

        _sources = BuildSources();
    }

    public string RepoRoot { get; }
    public string DataRoot { get; }
    public string DbPath { get; }
    public string DbVersion { get; }
    public string? PakPath { get; }
    public string PakVersion { get; }

    public IReadOnlyList<Source> Sources => _sources;

    /// <summary>Builds the catalog from the environment (see class doc for keys).</summary>
    public static SourceCatalog FromEnvironment()
    {
        var repoRoot = ResolveRepoRoot(Environment.GetEnvironmentVariable("AAEMU_ROOT"));
        var dataRoot = Environment.GetEnvironmentVariable("ARCHEAGE_DATA_ROOT")
            ?? Path.Combine(repoRoot, "AAEmu.Game", "Data");
        var dbPath = Environment.GetEnvironmentVariable("ARCHEAGE_DB_PATH")
            ?? Path.Combine(dataRoot, "compact.sqlite3");
        var dbVersion = Environment.GetEnvironmentVariable("ARCHEAGE_DB_VERSION") ?? DefaultDbVersion;
        var pakPath = Environment.GetEnvironmentVariable("ARCHEAGE_PAK_PATH");
        var pakVersion = Environment.GetEnvironmentVariable("ARCHEAGE_PAK_VERSION") ?? DefaultPakVersion;

        var extraRoots = new Dictionary<string, string>(StringComparer.Ordinal);
        var extra = Environment.GetEnvironmentVariable("ARCHEAGE_EXTRA_ROOTS");
        if (!string.IsNullOrWhiteSpace(extra))
        {
            foreach (var entry in extra.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var eq = entry.IndexOf('=');
                if (eq <= 0)
                    continue;
                var id = entry[..eq].Trim();
                var path = entry[(eq + 1)..].Trim();
                // Root ids must be simple identifiers so they can never be
                // confused with a path or a reserved id.
                if (id.Length > 0 && path.Length > 0 && IsValidRootId(id))
                    extraRoots[id] = Path.GetFullPath(path);
            }
        }

        return new SourceCatalog(repoRoot, dataRoot, dbPath, dbVersion, extraRoots, pakPath, pakVersion);
    }

    /// <summary>Resolves a root id to its absolute path, or null when unknown.</summary>
    public string? ResolveRoot(string? rootId)
        => rootId is not null && _roots.TryGetValue(rootId, out var path) ? path : null;

    /// <summary>Root ids must be simple identifiers (letters, digits, '-', '_').</summary>
    internal static bool IsValidRootId(string id)
        => id.Length > 0 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    /// <summary>All root ids in catalog order (deterministic).</summary>
    public IReadOnlyList<string> RootIds => _roots.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();

    /// <summary>
    /// True when <paramref name="fullPath"/> (already absolute) is inside one
    /// of the allowlisted roots. Used as the final guard for read_file and
    /// search_files after path normalization.
    /// </summary>
    public bool IsAllowed(string fullPath)
    {
        foreach (var root in _roots.Values)
        {
            if (IsWithin(root, fullPath))
                return true;
        }

        return false;
    }

    private List<Source> BuildSources()
    {
        var sources = new List<Source>
        {
            DbSource(),
            RootSource("data", "directory", DataRoot, "World spawns, XML/JSON templates, portal/path data",
                "1.2", "JSON/XML/plaintext", searchable: true,
                "ARCHEAGE_DATA_ROOT or <AAEMU_ROOT>/AAEmu.Game/Data"),
            RootSource("game-source", "directory", _roots["game-source"], "AAEmu.Game C# source (packets, managers, GameData, models)",
                "fork develop", "UTF-8 C#", searchable: true,
                "repo-local source root"),
            RootSource("sql", "directory", _roots["sql"], "MySQL schema + updates + compact.sqlite3 patches",
                "fork develop", "SQL", searchable: true,
                "schema source only; MySQL is mutable state and is NOT exposed"),
            RootSource("tools", "directory", _roots["tools"], "Python archaeology tooling (quest-graph, gamedata-graph, scorecard)",
                "fork-local", "Python 3", searchable: true,
                "graph builders; graphify-out/ is gitignored and absent"),
            RootSource("scripts", "directory", _roots["scripts"], "Census shell scripts + MCP smoke scripts",
                "fork-local", "Shell", searchable: true,
                "read-only sqlite3 census queries"),
            RootSource("scorecard-explorations", "directory", _roots["scorecard-explorations"],
                "Dossiers, evidence reports, capability matrix",
                "fork-local", "Markdown/JSON/JSONL", searchable: true,
                "pre-digested canonical archaeology"),
        };

        // The AAPak archive is a catalog source when configured.
        if (!string.IsNullOrWhiteSpace(PakPath))
            sources.Add(PakSource());

        // Explicitly allowlisted extra roots (operator opt-in).
        foreach (var (id, path) in _roots)
        {
            if (DefaultRootIds.Contains(id))
                continue;
            sources.Add(RootSource(id, "directory", path, "operator-allowlisted extra root",
                "external", "unknown", searchable: true,
                "added via ARCHEAGE_EXTRA_ROOTS"));
        }

        return sources;
    }

    private Source DbSource()
    {
        var size = File.Exists(DbPath) ? new FileInfo(DbPath).Length : 0;
        return new Source(
            SourceId: "compact.sqlite3",
            SourceType: "sqlite",
            Path: DbPath,
            LogicalDomain: "All game templates (items, NPCs, skills, quests, doodads, buffs, loots, spawners, zones, localized text)",
            Version: DbVersion,
            Encoding: "SQLite 3",
            Size: size,
            Searchable: false,
            Notes: "Canonical read-only reference DB (ARCHEAGE_DB_PATH); opened Mode=ReadOnly; never written");
    }

    private Source PakSource()
    {
        var size = File.Exists(PakPath!) ? new FileInfo(PakPath!).Length : 0;
        return new Source(
            SourceId: "game_pak",
            SourceType: "aapak",
            Path: PakPath!,
            LogicalDomain: "Full client asset pack (models, geodata, UI scripts, strings)",
            Version: PakVersion,
            Encoding: "AAPak archive (binary; entries listable, contents not text-parseable)",
            Size: size,
            Searchable: false,
            Notes: "ARCHEAGE_PAK_PATH; opened read-only; only the file table is read into memory; contents never streamed wholesale");
    }

    private static Source RootSource(string id, string type, string path, string domain,
        string version, string encoding, bool searchable, string notes)
    {
        var size = Directory.Exists(path) ? DirSize(path) : 0;
        return new Source(id, type, path, domain, version, encoding, size, searchable, notes);
    }

    private static long DirSize(string path)
    {
        long total = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try
                {
                    total += new FileInfo(file).Length;
                }
                catch (IOException)
                {
                    // Unreadable file — skip; catalog size is best-effort.
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            // Root unreadable — report 0.
        }

        return total;
    }

    private static bool IsWithin(string root, string path)
    {
        var rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var pathFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return pathFull.Equals(rootFull, StringComparison.Ordinal)
            || pathFull.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static string ResolveRepoRoot(string? envRoot)
    {
        if (!string.IsNullOrWhiteSpace(envRoot))
            return Path.GetFullPath(envRoot);

        // Walk up from the app base dir looking for the solution marker.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "AAEmu.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }

        return AppContext.BaseDirectory;
    }
}
