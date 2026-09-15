#nullable enable

using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

using AAEmu.Commons.Utils;

using NLog;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// Continental Road Network Service for AAEmu.
/// Loads digitized in-game road junctions and edges across Nuia and Haranya,
/// providing spatial queries and A* pathfinding along the road graph.
/// </summary>
public class RoadNetworkService : Singleton<RoadNetworkService>, IRoadNetworkService
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    private const float DefaultGridCellSize = 1000f;
    private const string EmbeddedResourceName = "AAEmu.Game.Data.map_atlas_data.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private readonly List<RoadJunction> _junctions = [];
    private readonly Dictionary<string, RoadJunction> _junctionsById = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<(RoadJunction Neighbor, float Cost)>> _adjacency = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(int CellX, int CellY), List<RoadJunction>> _spatialGrid = new();
    private readonly float _gridCellSize;
    private int _edgeCount;

    public int JunctionCount => _junctions.Count;
    public int EdgeCount => _edgeCount;

    public RoadNetworkService() : this(null, null)
    {
    }

    public RoadNetworkService(string? dataFilePath, string? jsonContent = null, float gridCellSize = DefaultGridCellSize)
    {
        _gridCellSize = gridCellSize > 0f ? gridCellSize : DefaultGridCellSize;
        LoadNetwork(dataFilePath, jsonContent);
    }

    public RoadJunction? FindNearestJunction(Vector3 position, float maxRadius = 2000f)
    {
        if (maxRadius <= 0f || _junctions.Count == 0)
            return null;

        var minCellX = (int)MathF.Floor((position.X - maxRadius) / _gridCellSize);
        var maxCellX = (int)MathF.Floor((position.X + maxRadius) / _gridCellSize);
        var minCellY = (int)MathF.Floor((position.Y - maxRadius) / _gridCellSize);
        var maxCellY = (int)MathF.Floor((position.Y + maxRadius) / _gridCellSize);

        RoadJunction? bestJunction = null;
        var bestDistSq = maxRadius * maxRadius;

        for (var cx = minCellX; cx <= maxCellX; cx++)
        {
            for (var cy = minCellY; cy <= maxCellY; cy++)
            {
                if (!_spatialGrid.TryGetValue((cx, cy), out var cellJunctions))
                    continue;

                foreach (var j in cellJunctions)
                {
                    var distSq = DistanceSquared(position, j);
                    if (distSq <= bestDistSq)
                    {
                        bestDistSq = distSq;
                        bestJunction = j;
                    }
                }
            }
        }

        return bestJunction;
    }

    public IReadOnlyList<RoadJunction> FindRoadPath(Vector3 start, Vector3 destination)
    {
        var startJunction = FindNearestJunction(start);
        var endJunction = FindNearestJunction(destination);

        if (startJunction == null || endJunction == null)
            return [];

        return FindRoadPath(startJunction, endJunction);
    }

    public IReadOnlyList<RoadJunction> FindRoadPath(RoadJunction start, RoadJunction destination)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(destination);

        if (start.Id.Equals(destination.Id, StringComparison.OrdinalIgnoreCase))
            return [start];

        var frontier = new PriorityQueue<RoadJunction, float>();
        var costSoFar = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        var cameFrom = new Dictionary<string, RoadJunction>(StringComparer.OrdinalIgnoreCase);
        var closed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        costSoFar[start.Id] = 0f;
        frontier.Enqueue(start, Heuristic(start.Position, destination.Position));

        while (frontier.Count > 0)
        {
            var current = frontier.Dequeue();

            if (current.Id.Equals(destination.Id, StringComparison.OrdinalIgnoreCase))
            {
                var path = new List<RoadJunction>();
                var curr = current;
                while (curr != null)
                {
                    path.Add(curr);
                    if (!cameFrom.TryGetValue(curr.Id, out curr!))
                        break;
                }
                path.Reverse();
                return path;
            }

            if (!closed.Add(current.Id))
                continue;

            if (!_adjacency.TryGetValue(current.Id, out var neighbors))
                continue;

            var currentCost = costSoFar[current.Id];

            foreach (var (neighbor, edgeCost) in neighbors)
            {
                if (closed.Contains(neighbor.Id))
                    continue;

                var newCost = currentCost + edgeCost;
                if (!costSoFar.TryGetValue(neighbor.Id, out var existingCost) || newCost < existingCost)
                {
                    costSoFar[neighbor.Id] = newCost;
                    cameFrom[neighbor.Id] = current;
                    var priority = newCost + Heuristic(neighbor.Position, destination.Position);
                    frontier.Enqueue(neighbor, priority);
                }
            }
        }

        return [];
    }

    public IReadOnlyList<RoadJunction> GetAllJunctions() => _junctions;

    public RoadJunction? GetJunctionById(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;

        return _junctionsById.GetValueOrDefault(id);
    }

    private static float DistanceSquared(Vector3 pos, RoadJunction j)
    {
        var dx = pos.X - j.X;
        var dy = pos.Y - j.Y;

        // If pos.Z is zero (e.g. 2D map query or altitude unprovided), compare horizontally
        if (MathF.Abs(pos.Z) < 0.001f)
        {
            return dx * dx + dy * dy;
        }

        var dz = pos.Z - j.Z;
        return dx * dx + dy * dy + dz * dz;
    }

    private static float Heuristic(Vector3 a, Vector3 b)
    {
        return Vector3.Distance(a, b);
    }

    private void LoadNetwork(string? explicitPath, string? explicitJson)
    {
        MapAtlasModel? model = null;

        if (!string.IsNullOrWhiteSpace(explicitJson))
        {
            model = JsonSerializer.Deserialize<MapAtlasModel>(explicitJson, SerializerOptions);
        }
        else
        {
            var (jsonText, stream) = ResolveDataStreamOrText(explicitPath);
            if (!string.IsNullOrEmpty(jsonText))
            {
                model = JsonSerializer.Deserialize<MapAtlasModel>(jsonText, SerializerOptions);
            }
            else if (stream != null)
            {
                using (stream)
                {
                    model = JsonSerializer.Deserialize<MapAtlasModel>(stream, SerializerOptions);
                }
            }
        }

        if (model == null || model.Junctions == null || model.Junctions.Count == 0)
        {
            throw new InvalidOperationException("Failed to load road network atlas data: dataset is empty or unresolvable.");
        }

        _junctions.Clear();
        _junctionsById.Clear();
        _adjacency.Clear();
        _spatialGrid.Clear();

        foreach (var j in model.Junctions)
        {
            _junctions.Add(j);
            _junctionsById[j.Id] = j;
            _adjacency[j.Id] = [];

            var cellX = (int)MathF.Floor(j.X / _gridCellSize);
            var cellY = (int)MathF.Floor(j.Y / _gridCellSize);
            if (!_spatialGrid.TryGetValue((cellX, cellY), out var bucket))
            {
                bucket = [];
                _spatialGrid[(cellX, cellY)] = bucket;
            }
            bucket.Add(j);
        }

        _edgeCount = 0;
        if (model.Edges != null)
        {
            _edgeCount = model.Edges.Count;
            foreach (var edge in model.Edges)
            {
                if (_junctionsById.TryGetValue(edge.From, out var fromJunction) &&
                    _junctionsById.TryGetValue(edge.To, out var toJunction))
                {
                    var cost = Vector3.Distance(fromJunction.Position, toJunction.Position);

                    // Road network is bidirectional
                    _adjacency[edge.From].Add((toJunction, cost));
                    _adjacency[edge.To].Add((fromJunction, cost));
                }
                else
                {
                    Logger.Warn($"Road edge references unknown junction: {edge.From} -> {edge.To}");
                }
            }
        }

        Logger.Info($"[RoadNetworkService] Initialized road network: {_junctions.Count} junctions, {_edgeCount} edges, {_spatialGrid.Count} spatial cells.");
    }

    private static (string? JsonText, System.IO.Stream? Stream) ResolveDataStreamOrText(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath))
        {
            return (File.ReadAllText(explicitPath), null);
        }

        string[] candidateRelativePaths =
        [
            Path.Combine("playertrace-coverage", "map_atlas_data.json"),
            Path.Combine("Data", "map_atlas_data.json"),
            Path.Combine("Data", "Navigation", "map_atlas_data.json")
        ];

        // Search working directory
        foreach (var rel in candidateRelativePaths)
        {
            var p = Path.Combine(Directory.GetCurrentDirectory(), rel);
            if (File.Exists(p))
                return (File.ReadAllText(p), null);
        }

        // Search AppContext.BaseDirectory
        foreach (var rel in candidateRelativePaths)
        {
            var p = Path.Combine(AppContext.BaseDirectory, rel);
            if (File.Exists(p))
                return (File.ReadAllText(p), null);
        }

        // Traverse upwards to repo root
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            foreach (var rel in candidateRelativePaths)
            {
                var p = Path.Combine(dir.FullName, rel);
                if (File.Exists(p))
                    return (File.ReadAllText(p), null);
            }
            dir = dir.Parent;
        }

        // Traverse upwards from CurrentDirectory
        dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir != null)
        {
            foreach (var rel in candidateRelativePaths)
            {
                var p = Path.Combine(dir.FullName, rel);
                if (File.Exists(p))
                    return (File.ReadAllText(p), null);
            }
            dir = dir.Parent;
        }

        // Fallback: Embedded Resource
        var asm = typeof(RoadNetworkService).Assembly;
        var stream = asm.GetManifestResourceStream(EmbeddedResourceName);
        if (stream != null)
            return (null, stream);

        var altName = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("map_atlas_data.json", StringComparison.OrdinalIgnoreCase));
        if (altName != null)
        {
            stream = asm.GetManifestResourceStream(altName);
            if (stream != null)
                return (null, stream);
        }

        return (null, null);
    }

    private sealed record MapAtlasModel(
        [property: JsonPropertyName("junctions")] List<RoadJunction>? Junctions,
        [property: JsonPropertyName("edges")] List<MapAtlasEdgeModel>? Edges
    );

    private sealed record MapAtlasEdgeModel(
        [property: JsonPropertyName("from")] string From,
        [property: JsonPropertyName("to")] string To,
        [property: JsonPropertyName("route")] string? Route
    );
}
