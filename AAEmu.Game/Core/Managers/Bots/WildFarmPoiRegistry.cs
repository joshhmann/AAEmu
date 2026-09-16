using System.Numerics;
using System.Text.Json;

using NLog;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// Point of Interest representation for wilderness/illegal tree farms and secret gathering spots.
/// </summary>
public sealed record WildFarmPoi(
    string Id,
    string Label,
    float X,
    float Y,
    float Z,
    string Description,
    uint ZoneId = 0);

/// <summary>
/// Service providing access to secret mountain farms, illegal tree groves, and wilderness POIs.
/// Used by NeedsFarmActivityModule and autonomous bot scouts to select remote planting destinations.
/// </summary>
public interface IWildFarmPoiRegistry
{
    IReadOnlyList<WildFarmPoi> GetAllPois();
    WildFarmPoi? FindNearest(Vector3 position, float maxRadius = 5000f);
    IReadOnlyList<WildFarmPoi> GetByZone(uint zoneId);
    int Count { get; }
}

public sealed class WildFarmPoiRegistry : IWildFarmPoiRegistry
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    private readonly List<WildFarmPoi> _pois = [];

    public WildFarmPoiRegistry(string? jsonPath = null)
    {
        Load(jsonPath);
    }

    public int Count => _pois.Count;

    public IReadOnlyList<WildFarmPoi> GetAllPois() => _pois;

    public WildFarmPoi? FindNearest(Vector3 position, float maxRadius = 5000f)
    {
        WildFarmPoi? best = null;
        var bestDistSq = maxRadius * maxRadius;

        foreach (var p in _pois)
        {
            var dx = p.X - position.X;
            var dy = p.Y - position.Y;
            var dsq = dx * dx + dy * dy;
            if (dsq < bestDistSq)
            {
                bestDistSq = dsq;
                best = p;
            }
        }

        return best;
    }

    public IReadOnlyList<WildFarmPoi> GetByZone(uint zoneId)
    {
        return _pois.FindAll(p => p.ZoneId == zoneId || p.ZoneId == 0);
    }

    private void Load(string? path)
    {
        _pois.Clear();

        // 1. Try specified or default file path
        var candidatePaths = new List<string>();
        if (!string.IsNullOrWhiteSpace(path))
            candidatePaths.Add(path);

        candidatePaths.Add(Path.Combine(AppContext.BaseDirectory, "playertrace-coverage", "map_atlas_data.json"));
        candidatePaths.Add(Path.Combine(Directory.GetCurrentDirectory(), "playertrace-coverage", "map_atlas_data.json"));
        candidatePaths.Add(Path.Combine(Directory.GetCurrentDirectory(), "Data", "map_atlas_data.json"));

        var loaded = false;
        foreach (var candidate in candidatePaths)
        {
            if (File.Exists(candidate))
            {
                try
                {
                    var text = File.ReadAllText(candidate);
                    using var doc = JsonDocument.Parse(text);
                    if (doc.RootElement.TryGetProperty("pois", out var poisElement) && poisElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var elem in poisElement.EnumerateArray())
                        {
                            var type = elem.TryGetProperty("type", out var t) ? t.GetString() : "";
                            if (type == "illegal_tree_farm")
                            {
                                var id = elem.GetProperty("id").GetString() ?? "";
                                var label = elem.TryGetProperty("label", out var l) ? l.GetString() ?? id : id;
                                var x = (float)elem.GetProperty("x").GetDouble();
                                var y = (float)elem.GetProperty("y").GetDouble();
                                var z = (float)elem.GetProperty("z").GetDouble();
                                var desc = elem.TryGetProperty("what", out var w) ? w.GetString() ?? "" : "";
                                _pois.Add(new WildFarmPoi(id, label, x, y, z, desc));
                            }
                        }
                        if (_pois.Count > 0)
                        {
                            Logger.Info($"[WildFarmPoiRegistry] Loaded {_pois.Count} secret wild farm POIs from {candidate}");
                            loaded = true;
                            break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn(ex, $"[WildFarmPoiRegistry] Failed parsing POIs from {candidate}");
                }
            }
        }

        // 2. Embedded fallback if file is absent (ensures tests & offline mode function seamlessly)
        if (!loaded || _pois.Count == 0)
        {
            _pois.AddRange(GetEmbeddedFallbackPois());
            Logger.Info($"[WildFarmPoiRegistry] Initialized {_pois.Count} default embedded wild farm POIs.");
        }
    }

    private static IEnumerable<WildFarmPoi> GetEmbeddedFallbackPois()
    {
        yield return new WildFarmPoi(
            "wild-farm-solzreed-plateau",
            "Solzreed Secret High Plateau (Illegal Tree Farm)",
            14210.5f, 14680.2f, 142.5f,
            "High mountain ledge above road loop; secluded forest canopy ideal for secret cedar/pine groves.",
            179);

        yield return new WildFarmPoi(
            "wild-farm-solzreed-cove",
            "Solzreed Hidden Coastal Hollow (Illegal Tree Farm)",
            14650.0f, 14720.0f, 95.0f,
            "Depressed coastal valley shielded from road line-of-sight; popular spot for wild aspen/yew trees.",
            179);

        yield return new WildFarmPoi(
            "wild-farm-solzreed-waterfall",
            "Solzreed Western Falls Clearing (Illegal Tree Farm)",
            13890.0f, 14350.0f, 118.0f,
            "Hidden glen tucked behind granite boulders near the western riverbed; damp soil, fast growth.",
            179);

        yield return new WildFarmPoi(
            "wild-farm-arcum-canyon",
            "Arcum Iris Canyon Crevice (Illegal Tree Farm)",
            21450.0f, 7120.0f, 245.0f,
            "Concealed canyon pocket between sand dunes and mountain wall; great for arid-climate wild saplings.",
            33);

        yield return new WildFarmPoi(
            "wild-farm-gweonid-terrace",
            "Gweonid High Lake Terrace (Illegal Tree Farm)",
            15200.0f, 13100.0f, 210.0f,
            "Elevated mountain terrace above Lake Aster; lush temperate biome, high lightning risk for thunderstruck.",
            181);

        yield return new WildFarmPoi(
            "wild-farm-tigerspine-ridge",
            "Tigerspine Hidden Quarry Ridge (Illegal Tree Farm)",
            22300.0f, 8400.0f, 185.0f,
            "Rocky shelf overlooking the cobalt quarry; tucked behind jagged crags where patrols rarely look.",
            36);
    }
}
