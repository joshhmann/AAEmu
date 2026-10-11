#nullable enable

using System.Numerics;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>A road segment whose full geometry has explicit navigation evidence.</summary>
public sealed record RoadTravelSegment(
    string Id, uint WorldId, string From, string To,
    IReadOnlyList<Vector3> Points, float LengthM, IReadOnlyList<uint> ZoneKeys, string Evidence);

/// <summary>A validated ordinary navigation approach/departure, with observed extra cost in seconds.</summary>
public sealed record RoadTravelConnector(IReadOnlyList<Vector3> Points, float AdditionalSeconds);

/// <summary>
/// Facts for one route decision. Unknown danger returns null and withholds that edge;
/// a known zero means no additional cost. Costs are seconds, never negative rewards.
/// Connector points must come from ordinary navigation in the actor's world instance.
/// Revision changes when relevant danger, movement capability or geometry changes.
/// </summary>
public sealed record RoadTravelQuery(uint WorldId, uint InstanceId, float SpeedMps)
{
    public long Revision { get; init; }
    public float MaxConnectorDistanceM { get; init; } = 2000f;
    public Func<Vector3, Vector3, RoadTravelConnector?>? FindConnector { get; init; }
    public Func<RoadTravelSegment, float?>? AdditionalSeconds { get; init; }
}

/// <summary>Measured road geometry plus approach/departure, not predicted arrival.</summary>
public sealed record RoadTravelRoute(
    IReadOnlyList<Vector3> Points, IReadOnlyList<string> SegmentIds,
    float DistanceM, float CostSeconds, long Revision);
