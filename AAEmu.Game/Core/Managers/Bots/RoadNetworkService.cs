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
    private readonly Dictionary<string, List<RoadTravelSegment>> _travelEdges = new(StringComparer.OrdinalIgnoreCase);
    private readonly float _gridCellSize;

    public int VerifiedTravelEdgeCount => _travelEdges.Values.Sum(e => e.Count);
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

    private void LoadTravelEdge(MapAtlasEdgeModel edge, RoadJunction from, RoadJunction to)
    {
        if (!edge.WorldId.HasValue || from.WorldId != edge.WorldId || to.WorldId != edge.WorldId
            || edge.TravelMode != "walk" || string.IsNullOrWhiteSpace(edge.NavigationEvidence)
            || string.IsNullOrWhiteSpace(edge.Route) || edge.Points is not { Count: >= 2 and <= 10000 })
            return;
        if (edge.Points.Any(p => p.Length != 3 || p.Any(n => !float.IsFinite(n))))
            return;
        var points = edge.Points.Select(p => new Vector3(p[0], p[1], p[2])).ToArray();
        // Near-looking endpoints are not joins. Exported segments must terminate at their actual junctions.
        if (Vector3.Distance(points[0], from.Position) > 0.01f || Vector3.Distance(points[^1], to.Position) > 0.01f)
            return;
        if (!float.IsFinite(Length(points)))
            return;
        var segment = new RoadTravelSegment(edge.Route, edge.WorldId.Value, from.Id, to.Id,
            points, Length(points), edge.ZoneKeys.ToArray(), edge.NavigationEvidence);
        _travelEdges[from.Id].Add(segment);
        if (edge.Bidirectional)
            _travelEdges[to.Id].Add(segment with { From = to.Id, To = from.Id, Points = points.Reverse().ToArray() });
    }

    public RoadTravelRoute? FindTravelRoute(Vector3 start, Vector3 destination, RoadTravelQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!Finite(start) || !Finite(destination) || !float.IsFinite(query.SpeedMps) || query.SpeedMps <= 0
            || !float.IsFinite(query.MaxConnectorDistanceM) || query.MaxConnectorDistanceM <= 0)
            return null;
        // Bound connector work. A failed nearest approach does not conceal another usable nearby junction.
        var starts = Candidates(start, query).Select(j => (Junction: j, Connector: Connector(start, j.Position, query)))
            .Where(c => c.Connector != null).ToArray();
        var ends = Candidates(destination, query).Select(j => (Junction: j, Connector: Connector(j.Position, destination, query)))
            .Where(c => c.Connector != null).ToDictionary(c => c.Junction.Id, c => c.Connector!, StringComparer.OrdinalIgnoreCase);
        if (starts.Length == 0 || ends.Count == 0)
            return null;

        var queue = new PriorityQueue<string, (float Cost, long Sequence)>();
        var costs = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        var previous = new Dictionary<string, RoadTravelSegment>(StringComparer.OrdinalIgnoreCase);
        var approaches = new Dictionary<string, IReadOnlyList<Vector3>>(StringComparer.OrdinalIgnoreCase);
        long sequence = 0;
        foreach (var candidate in starts)
        {
            var cost = Length(candidate.Connector!.Points) / query.SpeedMps + candidate.Connector.AdditionalSeconds;
            costs[candidate.Junction.Id] = cost;
            approaches[candidate.Junction.Id] = candidate.Connector!.Points;
            queue.Enqueue(candidate.Junction.Id, (cost, sequence++));
        }
        string? bestEnd = null;
        var bestCost = float.PositiveInfinity;
        // Zero heuristic: additional costs are seconds and can vary by character/world state.
        while (queue.TryDequeue(out var current, out var priority))
        {
            if (priority.Cost != costs[current])
                continue;
            if (priority.Cost > bestCost)
                break;
            if (ends.TryGetValue(current, out var departure))
            {
                var total = priority.Cost + Length(departure.Points) / query.SpeedMps + departure.AdditionalSeconds;
                if (total < bestCost) { bestCost = total; bestEnd = current; }
            }
            foreach (var edge in _travelEdges[current])
            {
                if (edge.WorldId != query.WorldId)
                    continue;
                var extra = query.AdditionalSeconds?.Invoke(edge);
                if (!extra.HasValue || !float.IsFinite(extra.Value) || extra.Value < 0)
                    continue;
                var next = priority.Cost + edge.LengthM / query.SpeedMps + extra.Value;
                if (!float.IsFinite(next) || (costs.TryGetValue(edge.To, out var old) && old <= next))
                    continue;
                costs[edge.To] = next;
                previous[edge.To] = edge;
                queue.Enqueue(edge.To, (next, sequence++));
            }
        }
        if (bestEnd == null)
            return null;
        var segments = new List<RoadTravelSegment>();
        var cursor = bestEnd;
        while (previous.TryGetValue(cursor, out var edge)) { segments.Add(edge); cursor = edge.From; }
        segments.Reverse();
        var routePoints = new List<Vector3>();
        Append(routePoints, approaches[cursor]);
        foreach (var segment in segments) Append(routePoints, segment.Points);
        Append(routePoints, ends[bestEnd].Points);
        return new RoadTravelRoute(routePoints.ToArray(), segments.Select(s => s.Id).ToArray(),
            Length(routePoints), bestCost, query.Revision);
    }

    private IEnumerable<RoadJunction> Candidates(Vector3 position, RoadTravelQuery query)
    {
        // Geometry is cached at startup. The bounded spatial buckets avoid a world scan per wake.
        var radius = Math.Min(query.MaxConnectorDistanceM, 2000f);
        var minX = (int)MathF.Floor((position.X - radius) / _gridCellSize);
        var maxX = (int)MathF.Floor((position.X + radius) / _gridCellSize);
        var minY = (int)MathF.Floor((position.Y - radius) / _gridCellSize);
        var maxY = (int)MathF.Floor((position.Y + radius) / _gridCellSize);
        var found = new List<RoadJunction>();
        for (var x = minX; x <= maxX; x++)
            for (var y = minY; y <= maxY; y++)
                if (_spatialGrid.TryGetValue((x, y), out var bucket))
                    found.AddRange(bucket.Where(j => j.WorldId == query.WorldId
                        && Vector3.Distance(position, j.Position) <= radius));
        return found.OrderBy(j => Vector3.DistanceSquared(position, j.Position))
            .ThenBy(j => j.Id, StringComparer.Ordinal).Take(8);
    }

    private static RoadTravelConnector? Connector(Vector3 from, Vector3 to, RoadTravelQuery query)
    {
        if (Vector3.Distance(from, to) <= 0.01f)
            return new RoadTravelConnector(new[] { from, to }, 0);
        var connector = query.FindConnector?.Invoke(from, to);
        var points = connector?.Points;
        if (connector == null || !float.IsFinite(connector.AdditionalSeconds) || connector.AdditionalSeconds < 0
            || points is not { Count: >= 2 and <= 10000 } || points.Any(p => !Finite(p))
            || Vector3.Distance(from, points[0]) > 0.01f || Vector3.Distance(to, points[^1]) > 0.01f)
            return null;
        return new RoadTravelConnector(points.ToArray(), connector.AdditionalSeconds);
    }

    private static void Append(List<Vector3> target, IReadOnlyList<Vector3> points)
    {
        foreach (var point in points)
            if (target.Count == 0 || target[^1] != point) target.Add(point);
    }

    private static float Length(IReadOnlyList<Vector3> points)
    {
        var length = 0f;
        for (var i = 1; i < points.Count; i++) length += Vector3.Distance(points[i - 1], points[i]);
        return length;
    }

    private static bool Finite(Vector3 point) => float.IsFinite(point.X) && float.IsFinite(point.Y) && float.IsFinite(point.Z);

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
            _travelEdges[j.Id] = [];

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

                    LoadTravelEdge(edge, fromJunction, toJunction);

                    // Legacy candidate inspection graph is bidirectional
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
    )
    {
        [JsonPropertyName("world_id")] public uint? WorldId { get; init; }
        [JsonPropertyName("points")] public List<float[]>? Points { get; init; }
        [JsonPropertyName("navigation_evidence")] public string? NavigationEvidence { get; init; }
        [JsonPropertyName("travel_mode")] public string? TravelMode { get; init; }
        [JsonPropertyName("bidirectional")] public bool Bidirectional { get; init; }
        [JsonPropertyName("zone_keys")] public List<uint> ZoneKeys { get; init; } = [];
    }
}
