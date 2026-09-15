#nullable enable

using System.Numerics;
using System.Text.Json.Serialization;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// A digitized road junction connecting routes across Nuia and Haranya.
/// </summary>
public sealed record RoadJunction
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; init; } = string.Empty;

    [JsonPropertyName("x")]
    public float X { get; init; }

    [JsonPropertyName("y")]
    public float Y { get; init; }

    [JsonPropertyName("z")]
    public float Z { get; init; }

    [JsonPropertyName("routes")]
    public IReadOnlyList<string> Routes { get; init; } = [];

    [JsonIgnore]
    public Vector3 Position => new(X, Y, Z);

    public RoadJunction() { }

    public RoadJunction(string id, string label, float x, float y, float z, IReadOnlyList<string>? routes = null)
    {
        Id = id;
        Label = label;
        X = x;
        Y = y;
        Z = z;
        Routes = routes ?? [];
    }

    public override string ToString() => $"{Label} ({Id}) at ({X:F1}, {Y:F1}, {Z:F1})";
}

/// <summary>
/// Service providing road network topology, spatial search, and A* pathfinding
/// across the continental road network of ArcheAge (Nuia and Haranya).
/// </summary>
public interface IRoadNetworkService
{
    /// <summary>
    /// Total number of road junctions loaded in the network.
    /// </summary>
    int JunctionCount { get; }

    /// <summary>
    /// Total number of road edges loaded in the network.
    /// </summary>
    int EdgeCount { get; }

    /// <summary>
    /// Finds the nearest road junction to the given position within <paramref name="maxRadius"/> meters.
    /// Returns null if no junction is found within the radius.
    /// </summary>
    RoadJunction? FindNearestJunction(Vector3 position, float maxRadius = 2000f);

    /// <summary>
    /// Finds a shortest path of road junctions between start and destination positions
    /// using A* pathfinding on the junction edge graph.
    /// </summary>
    IReadOnlyList<RoadJunction> FindRoadPath(Vector3 start, Vector3 destination);

    /// <summary>
    /// Finds a shortest path between two specific junctions using A* pathfinding.
    /// </summary>
    IReadOnlyList<RoadJunction> FindRoadPath(RoadJunction start, RoadJunction destination);

    /// <summary>
    /// Returns all registered road junctions.
    /// </summary>
    IReadOnlyList<RoadJunction> GetAllJunctions();
}
