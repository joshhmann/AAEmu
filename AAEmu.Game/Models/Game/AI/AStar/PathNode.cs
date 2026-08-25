// https://lsreg.ru/realizaciya-algoritma-poiska-a-na-c/

using System.Numerics;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Utils;

namespace AAEmu.Game.Models.Game.AI.AStar;

/// <summary>
/// Reusable A* pathfinder.
/// </summary>
public class PathNode
{
    /// <summary>
    /// Diagnostics: number of nodes expanded (moved from open to closed) during the last
    /// FindPath call on this instance. For measurement rigs and tests; not used for routing.
    /// </summary>
    public long ExpandedNodesLastSearch { get; private set; }
    /// <summary>
    /// Current zone.Id 
    /// </summary>
    public uint ZoneKey { get; set; }

    /// <summary>
    /// The current point on the map.
    /// </summary>
    public Vector3 CurrentTargetPos { get; set; }

    /// <summary>
    /// Coordinates of the start point on the map (for the script).
    /// </summary>
    public Vector3 StartPointPos { get; set; } = Vector3.Zero;

    /// <summary>
    /// Coordinates of the end point on the map (for the script).
    /// </summary>
    public Vector3 EndPointPos { get; set; } = Vector3.Zero;

    /// <summary>
    /// List of found points (for the script).
    /// </summary>
    public Queue<Vector3> FoundPath { get; set; } = [];

    /// <summary>
    /// The coordinates of the point on the map. And the coordinates of the point on the map where the Npc goes.
    /// </summary>
    public Vector3 Position { get; set; }

    /// <summary>
    /// Path length from the start (G).
    /// </summary>
    private float PathLengthFromStart { get; set; }

    /// <summary>
    /// The point from which it came to this point.
    /// </summary>
    private PathNode CameFrom { get; set; }

    /// <summary>
    /// Approximate distance to target (H).
    /// </summary>
    private float PathLengthToEnd { get; init; }

    /// <summary>
    /// Expected total distance to target (F).
    /// </summary>
    private float EstimateFullPathLength => PathLengthFromStart + PathLengthToEnd;

    /// <summary>
    /// Basic method of route calculation.
    /// </summary>
    /// <param name="world"></param>
    /// <param name="start"></param>
    /// <param name="goal"></param>
    /// <returns></returns>
    public List<Vector3> FindPath(WorldInstance world, Vector3 start, Vector3 goal)
    {
        // Find the nearest point from the start point in the list of geodata points and start the search from it.
        var posStart = world.Template.GeoData.FindСlosestToTheCurrent(ZoneKey, new Vector3(start.X, start.Y, start.Z));
        if (posStart != null)
            start = posStart.Pos; // replace it with the nearest point from the geodata
        var posEnd = world.Template.GeoData.FindСlosestToTheCurrent(ZoneKey, new Vector3(goal.X, goal.Y, goal.Z));
        if (posEnd != null)
            goal = posEnd.Pos;// replace it with the nearest point from the geodata
        EndPointPos = goal;
        var rawDistance = Vector3.Distance(start, goal);

        ExpandedNodesLastSearch = 0;

        // Step 1.
        var closedPositions = new HashSet<Vector3>();
        var openByPosition = new Dictionary<Vector3, PathNode>();
        // Binary min-heap on F with an insertion-sequence tie-breaker, reproducing the
        // previous OrderBy(F)-stable selection order without rescanning the open set.
        // Improved nodes are re-pushed; their superseded entries are skipped as stale.
        var sequence = 0;
        var openSet = new PriorityQueue<PathNode, (float Estimate, int Sequence)>();

        // Step 2.
        var startNode = new PathNode
        {
            CurrentTargetPos = posStart?.Pos ?? start,
            Position = start,
            EndPointPos = goal,
            CameFrom = null,
            PathLengthFromStart = 0,
            PathLengthToEnd = GetHeuristicPathLength(start)
        };
        openSet.Enqueue(startNode, (startNode.EstimateFullPathLength, sequence++));
        openByPosition[startNode.Position] = startNode;

        var maxLoopsLeft = (int)MathF.Ceiling(rawDistance * 10) + 50; // This is to prevent the pathfinder from traveling too far off
        while (openSet.TryDequeue(out var currentNode, out _))
        {
            // Skip stale heap entries (closed or superseded by a cheaper path).
            if (closedPositions.Contains(currentNode.Position)
                || !openByPosition.TryGetValue(currentNode.Position, out var liveNode)
                || !ReferenceEquals(liveNode, currentNode))
            {
                continue;
            }

            maxLoopsLeft--;

            // Step 4.
            if (currentNode.Position.Equals(goal) || maxLoopsLeft <= 0)
            {
                var result = GetPathForNode(currentNode);
                // Leave the nearest point taken from geodata instead of the point from where we are going
                // result[0] = pos1; // replace the first and the last point with the real one
                // result[^1] = pos2;
                // Let's add the target coordinates to the found points
                result.Add(EndPointPos);
                result = AiGeoDataManager.DouglasPeuckerReduction(result, 2.0);
                Position = result[0];
                CurrentTargetPos = Vector3.Zero;
                return result;
            }

            // Step 5.
            openByPosition.Remove(currentNode.Position);
            closedPositions.Add(currentNode.Position);
            ExpandedNodesLastSearch++;

            // Step 6.
            foreach (var neighbourNode in GetNeighbours(world, currentNode))
            {
                // Step 7.
                if (closedPositions.Contains(neighbourNode.Position))
                {
                    continue;
                }

                if (!openByPosition.TryGetValue(neighbourNode.Position, out var openNode))
                {
                    // Step 8.
                    openByPosition.Add(neighbourNode.Position, neighbourNode);
                    openSet.Enqueue(neighbourNode, (neighbourNode.EstimateFullPathLength, sequence++));
                }
                else if (openNode.PathLengthFromStart > neighbourNode.PathLengthFromStart)
                {
                    // Step 9.
                    openNode.CameFrom = currentNode;
                    openNode.PathLengthFromStart = neighbourNode.PathLengthFromStart;
                    openSet.Enqueue(openNode, (openNode.EstimateFullPathLength, sequence++));
                }
            }
        }
        // Step 10.
        return [];
    }

    /// <summary>
    /// H: Estimates the distance to the target.
    /// </summary>
    /// <param name="from"></param>
    /// <returns></returns>
    private float GetHeuristicPathLength(Vector3 from)
    {
        // point-to-point distance
        var fromVector = new Vector3(from.X, from.Y, from.Z);
        var toVector = new Vector3(EndPointPos.X, EndPointPos.Y, EndPointPos.Z);
        return MathUtil.CalculateDistance(fromVector, toVector);
    }

    /// <summary>
    /// Obtaining a list of neighbors
    /// </summary>
    /// <param name="world"></param>
    /// <param name="pathNode"></param>
    /// <returns></returns>
    private List<PathNode> GetNeighbours(WorldInstance world, PathNode pathNode)
    {
        var result = new List<PathNode>();

        // Check which navmesh file is valid at this position
        var bai = world.Template.GetBaiByPos(pathNode.CurrentTargetPos);
        if (bai == null)
        {
            return result;
        }

        // Find the nearest node
        var nearestNode = bai.FindClosestNetMissionNode(pathNode.CurrentTargetPos);
        if (nearestNode == null)
        {
            // Was not able to find a nearby node
            // TODO: create a fall-back system
            return result;
        }

        // The adjacent points are the points where you can go.
        var neighbourPoints = world.Template.GeoData.GetAvailablePoints(nearestNode);

        foreach (var linkDescriptor in neighbourPoints)
        {
            // Checking that the point falls within the forbidden area where it is not allowed to walk.
            if (world.Template.GeoData.CheckImpossibleWalk(linkDescriptor.TargetNodeDescriptor.Pos))
            {
                //ViewPoint(point.Position, 858u); // let's show the point for debugging purposes
                continue;
            }

            // Fill in the data for the waypoint.
            var neighbourNode = new PathNode
            {
                CurrentTargetPos = linkDescriptor.TargetNodeDescriptor.Pos,
                Position = linkDescriptor.TargetNodeDescriptor.Pos,
                EndPointPos = pathNode.EndPointPos,
                CameFrom = pathNode,
                // G accumulates the walked cost from the start: parent G plus this link's
                // step length (the old code stored distance-to-goal here, which broke
                // A*'s cost model). H estimates the remaining distance to the goal.
                PathLengthFromStart = pathNode.PathLengthFromStart + Vector3.Distance(linkDescriptor.SourceNodeDescriptor.Pos, linkDescriptor.TargetNodeDescriptor.Pos),
                PathLengthToEnd = GetHeuristicPathLength(linkDescriptor.TargetNodeDescriptor.Pos)
            };

            result.Add(neighbourNode);
        }

        return result;
    }

    /// <summary>
    /// Obtaining a route. The route is represented as a list of point coordinates.
    /// </summary>
    /// <param name="pathNode"></param>
    /// <returns></returns>
    private static List<Vector3> GetPathForNode(PathNode pathNode)
    {
        var result = new List<Vector3>();
        var currentNode = pathNode;
        while (currentNode != null)
        {
            result.Add(currentNode.Position);
            //ViewPoint(currentNode.Position, 5014u); // let's show the point for debugging purposes
            currentNode = currentNode.CameFrom;
        }
        result.Reverse();

        return result;
    }
}
