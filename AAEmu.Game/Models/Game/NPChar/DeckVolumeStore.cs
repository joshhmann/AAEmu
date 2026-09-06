using System.Text.Json;
using System.Text.Json.Serialization;

using NLog;

namespace AAEmu.Game.Models.Game.NPChar;

/// <summary>
/// Phase 1 (audit-only) deck-floor hint: a named axis-aligned box carrying the
/// stand-surface Z the server is otherwise blind to (quay decks, ship decks,
/// bridge spans). Composes into the grounding height stack AFTER nav/terrain
/// with top precedence when the query point is inside the box.
/// DATA-side: entries are authored tour-log → data PR (never code per entry);
/// ships EMPTY until deck geometry is confirmed.
/// </summary>
public sealed record DeckVolume(
    string Name,
    float MinX, float MinY, float MinZ,
    float MaxX, float MaxY, float MaxZ,
    float FloorZ)
{
    public bool Contains(float x, float y, float z) =>
        x >= MinX && x <= MaxX &&
        y >= MinY && y <= MaxY &&
        z >= MinZ && z <= MaxZ;
}

internal sealed class DeckVolumeEntryJson
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }
    [JsonPropertyName("minX")]
    public float MinX { get; set; }
    [JsonPropertyName("minY")]
    public float MinY { get; set; }
    [JsonPropertyName("minZ")]
    public float MinZ { get; set; }
    [JsonPropertyName("maxX")]
    public float MaxX { get; set; }
    [JsonPropertyName("maxY")]
    public float MaxY { get; set; }
    [JsonPropertyName("maxZ")]
    public float MaxZ { get; set; }
    [JsonPropertyName("floorZ")]
    public float FloorZ { get; set; }
}

/// <summary>
/// Read-only store for <see cref="DeckVolume"/> hints. Loads lazily once from
/// <c>Data/Navigation/deck_volumes.json</c> (bare JSON array, may be empty);
/// a missing or unreadable file means "no hints", never an error.
/// </summary>
public static class DeckVolumeStore
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    public static string DataFilePath { get; set; } = Path.Combine("Data", "Navigation", "deck_volumes.json");

    private static readonly Lock Gate = new();
    private static List<DeckVolume> _volumes = [];
    private static bool _loaded;

    public static int Count
    {
        get
        {
            lock (Gate)
            {
                EnsureLoadedLocked();
                return _volumes.Count;
            }
        }
    }

    /// <summary>
    /// Returns the floor Z of the highest-floor volume containing the point.
    /// </summary>
    public static bool TryGetFloor(float x, float y, float z, out float floorZ)
    {
        lock (Gate)
        {
            EnsureLoadedLocked();
            floorZ = 0f;
            var found = false;
            foreach (var volume in _volumes)
            {
                if (!volume.Contains(x, y, z))
                    continue;
                if (!found || volume.FloorZ > floorZ)
                    floorZ = volume.FloorZ;
                found = true;
            }
            return found;
        }
    }

    public static void Reload()
    {
        lock (Gate)
        {
            _volumes = LoadFromFile();
            _loaded = true;
        }
    }

    private static void EnsureLoadedLocked()
    {
        if (_loaded)
            return;
        _volumes = LoadFromFile();
        _loaded = true;
    }

    private static List<DeckVolume> LoadFromFile()
    {
        try
        {
            if (!File.Exists(DataFilePath))
            {
                Logger.Info($"[DeckVolumeStore] No deck hint file at '{DataFilePath}'. No deck hints loaded.");
                return [];
            }

            var entries = JsonSerializer.Deserialize<List<DeckVolumeEntryJson>>(File.ReadAllText(DataFilePath));
            if (entries == null)
                return [];

            var volumes = new List<DeckVolume>(entries.Count);
            foreach (var entry in entries)
            {
                if (string.IsNullOrWhiteSpace(entry.Name)
                    || !float.IsFinite(entry.FloorZ) || entry.FloorZ <= 0f
                    || entry.MinX > entry.MaxX || entry.MinY > entry.MaxY || entry.MinZ > entry.MaxZ)
                {
                    Logger.Warn($"[DeckVolumeStore] Skipping invalid deck entry '{entry.Name ?? "<unnamed>"}' in '{DataFilePath}'.");
                    continue;
                }
                volumes.Add(new DeckVolume(entry.Name, entry.MinX, entry.MinY, entry.MinZ,
                    entry.MaxX, entry.MaxY, entry.MaxZ, entry.FloorZ));
            }

            Logger.Info($"[DeckVolumeStore] Loaded {volumes.Count} deck hint volumes from '{DataFilePath}'.");
            return volumes;
        }
        catch (Exception ex)
        {
            Logger.Warn($"[DeckVolumeStore] Failed to load '{DataFilePath}': {ex.Message}. No deck hints loaded.");
            return [];
        }
    }

    /// <summary>Test hook: replaces the volume set (marks loaded, skips file I/O).</summary>
    internal static void SetVolumesForTests(IReadOnlyList<DeckVolume> volumes)
    {
        lock (Gate)
        {
            _volumes = [.. volumes];
            _loaded = true;
        }
    }

    /// <summary>Test hook: clears state so the next query re-runs the lazy load.</summary>
    internal static void ResetForTests()
    {
        lock (Gate)
        {
            _volumes = [];
            _loaded = false;
        }
    }
}
