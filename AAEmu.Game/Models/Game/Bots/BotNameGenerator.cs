using System.Text.Json;
using System.Text.RegularExpressions;

using AAEmu.Commons.IO;

using NLog;

namespace AAEmu.Game.Models.Game.Bots;

/// <summary>
/// In-world CHARACTER names for provisioned bots. A bot citizen should read as
/// a person walking through Solzreed, not as a test fixture: the presence demo
/// used to mint <c>Citizen01</c>, <c>Citizen02</c>, … straight from a config
/// prefix plus a loop index, which reads as scaffolding in front of a human.
///
/// NAMES COME FROM DATA, NOT CODE. The pool lives in
/// <c>Data/Bots/bot_names.json</c> next to the presence manifest; each entry is
/// a word the game's own 1.2 NPC data already uses as a personal name (see the
/// provenance comment in that file). Editing the JSON re-skins the world with
/// no code change — the loader re-reads nothing per boot beyond the file itself.
///
/// UNQUENESS. A character name shares the human namespace
/// (<see cref="CharacterCreateError.NameAlreadyExists"/>), so a generated name
/// must never collide with a name a real player already holds. Generation is a
/// deterministic walk over the pool, and <see cref="NameIsTaken"/> probes the
/// live <see cref="Core.Managers.INameManager"/> registry so a taken name is
/// stepped over instead of failing provisioning. Colliding with a HUMAN's name
/// can therefore never happen; the DB stays authoritative because
/// <c>HeadlessSession.Provision</c> still re-validates and refuses a foreign row.
///
/// DETERMINISM. The same roster index always yields the same name on every
/// boot and every machine: the walk is a pure function of (roster index, pool,
/// taken-set). That is what keeps the restart-idempotency contract intact — a
/// hard-killed lane comes back up with the SAME citizens, adopting the rows the
/// previous boot created, instead of minting a fresh set and orphaning them.
/// Variety comes from the pool order (123 names) and the collision walk, not
/// from a time-varying RNG.
/// </summary>
public static partial class BotNameGenerator
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    /// <summary>Pool file, relative to the app directory (the same root Config.Local.json lives in).</summary>
    internal const string PoolFileRelativePath = "Data/Bots/bot_names.json";

    /// <summary>
    /// The subset of NameManager's default character-name regex
    /// (<c>^[a-zA-Z0-9а-яА-Я]{1,18}$</c>). Mirrored rather than referenced so
    /// pool validation stays hermetic — NameManager is a singleton that needs a
    /// DB-backed registry, and this class must be usable (and testable) without one.
    /// </summary>
    [GeneratedRegex("^[a-zA-Z0-9а-яА-Я]{1,18}$")]
    private static partial Regex CharacterNameRegex();

    private static readonly object LoadLock = new();
    private static List<string> _pool = [];

    /// <summary>
    /// The in-world name pool. Loaded once from <see cref="PoolFileRelativePath"/>;
    /// entries that do not satisfy the character-name rules are dropped (with a
    /// warning) rather than allowed to fail provisioning later. Empty when the
    /// file is absent — callers fall back to the caller-supplied prefix.
    /// </summary>
    public static IReadOnlyList<string> Pool
    {
        get
        {
            if (_pool.Count == 0)
            {
                lock (LoadLock)
                    _pool = LoadPool() ?? [];
            }
            return _pool;
        }
    }

    private static List<string> LoadPool()
    {
        var path = Path.Combine(FileManager.AppPath, PoolFileRelativePath);
        if (!File.Exists(path))
        {
            Logger.Warn("BotNameGenerator: name pool not found at {Path} — falling back to the configured prefix", path);
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("names", out var names) || names.ValueKind != JsonValueKind.Array)
            {
                Logger.Error("BotNameGenerator: {Path} has no \"names\" array", path);
                return null;
            }

            var valid = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var element in names.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.String)
                {
                    Logger.Warn("BotNameGenerator: skipping non-string pool entry");
                    continue;
                }
                var name = element.GetString();
                if (string.IsNullOrWhiteSpace(name) || !CharacterNameRegex().IsMatch(name))
                {
                    Logger.Warn("BotNameGenerator: skipping pool entry that fails the character-name rules");
                    continue;
                }
                if (seen.Add(name))
                    valid.Add(name);
            }

            if (valid.Count == 0)
            {
                Logger.Error("BotNameGenerator: {Path} yielded no usable names", path);
                return null;
            }

            Logger.Info("BotNameGenerator: loaded {Count} in-world character names", valid.Count);
            return valid;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            Logger.Error(ex, "BotNameGenerator: failed to read name pool {Path}", path);
            return null;
        }
    }

    /// <summary>
    /// True when the name is already registered in the live NameManager registry.
    /// Injected so name assignment stays hermetic; production passes
    /// <c>n =&gt; NameManager.Instance.GetCharacterId(n) != 0</c>.
    /// </summary>
    public delegate bool NameProbe(string name);

    /// <summary>
    /// Picks the character name for roster slot <paramref name="index"/>.
    ///
    /// <paramref name="namePrefix"/> keeps the historical behaviour: when a
    /// caller configures a prefix (the presence demo used "Citizen"), the name
    /// is <c>{prefix}{index+1:D2}</c> exactly as before, so any deployment that
    /// pinned a prefix — or any existing test/tool that greps for those names —
    /// is unaffected. With no prefix configured the name is drawn from the pool.
    ///
    /// The pool walk starts at <c>index % pool.Count</c> and steps forward until
    /// it finds an entry that is not already taken, so distinct slots get
    /// distinct names while the mapping stays a pure function of the index.
    /// </summary>
    public static string NameFor(int index, string? namePrefix, NameProbe? nameIsTaken = null)
    {
        if (!string.IsNullOrWhiteSpace(namePrefix))
            return $"{namePrefix}{index + 1:D2}";

        var pool = Pool;
        if (pool.Count == 0)
            return FallbackName(index);

        var start = ((index % pool.Count) + pool.Count) % pool.Count;
        for (var step = 0; step < pool.Count; step++)
        {
            var candidate = pool[(start + step) % pool.Count];
            if (nameIsTaken is null || !nameIsTaken(candidate))
                return candidate;
        }

        // Every pool name is taken: fall back to the index-suffixed shape rather
        // than returning a duplicate, which provisioning would reject anyway.
        return FallbackName(index);
    }

    /// <summary>
    /// Last-resort name when the pool is unusable (file missing/corrupt or fully
    /// claimed). Keeps the index-suffixed shape of the original demo naming so
    /// the caller always gets a distinct, valid name.
    /// </summary>
    internal static string FallbackName(int index) => $"Citizen{index + 1:D2}";
}