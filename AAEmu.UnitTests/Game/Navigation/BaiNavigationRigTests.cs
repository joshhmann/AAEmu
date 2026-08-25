using System.Collections.Concurrent;
using System.Numerics;
using System.Reflection;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.IO;
using AAEmu.Game.Models.CryEngine.Entities;
using AAEmu.Game.Models.CryEngine.Loaders;
using AAEmu.Game.Models.CryEngine.Readers;
using AAEmu.Game.Models.Game.AI.AStar;
using AAEmu.Game.Utils;
using TUnit.Core.Exceptions;

using AAEmu.Game.Models.Game.World;

namespace AAEmu.UnitTests.Game.Navigation;

/// <summary>
/// Headless navigation rig over REAL game data: loads a small set of real path-block
/// .bai files read-only from the deployed 1.2 game_pak through the REAL
/// NetMissionReader/AreasMissionReader parsers and drives the REAL
/// AiGeoDataManager + PathNode.FindPath chain.
///
/// Guards the navigation slice invariants:
///  - parser/link continuity on real data,
///  - spatial-index exactness vs linear scan,
///  - G-cost accumulation (fixed PathNode.cs:226) finds the cheap route,
///  - planned-path reachability agreement with BFS on the same subgraph,
///  - determinism (same input → same path).
///
/// Skips gracefully when no game_pak is mounted (CI machines without game data).
/// </summary>

public class BaiNavigationRigTests
{
    private const string PakPath = "/root/aaemu-e2e/runtime/game-data/ClientData/game_pak";
    private const int BlockSize = 256;
    private const int CellSize = 1024;

    // Real anchors from Data/Portal/respawns.json ("Pirate Respawn: Solzreed", "Respawn: Marianople")
    private static readonly Vector2 SolzreedShore = new(15232.6f, 15341.4f);
    private static readonly Vector2 Marianople = new(11022.2f, 12207.8f);

    private static WorldTemplate _template;
    private static WorldInstance _world;
    private static List<BaseBaiLoader> _solzreedLoaders;
    private static BaseBaiLoader _anySolzreedLoader;

    [Before(Class)]
    public static void SetUp()
    {
        if (!File.Exists(PakPath))
            return; // no game data on this machine — tests self-skip

        if (!ClientFileManager.AddSource(PakPath))
            return;

        _template = new WorldTemplate
        {
            Id = 0,
            Name = "main_world",
            CellX = 31,
            CellY = 31,
            MaxHeight = 1024f,
            HeightMaxCoefficient = 1f,
        };
        AllocateCells(_template);
        _template.GeoData = new AiGeoDataManager(_template);
        _world = new WorldInstance(_template, 0, true, 1);

        // Load a 3x3 block cluster around each anchor through the real loader chain.
        _solzreedLoaders = LoadBlockCluster(BlockOf(SolzreedShore));
        var marianopleLoaders = LoadBlockCluster(BlockOf(Marianople));
        if (_solzreedLoaders.Count == 0 || marianopleLoaders.Count == 0)
            throw new SkipTestException("no navgraph blocks parsed from game_pak cluster");
        _anySolzreedLoader = _solzreedLoaders[0];
    }

    private static bool RigAvailable => _world != null;

    private static (int X, int Y) BlockOf(Vector2 p)
        => ((int)MathF.Floor(p.X / BlockSize), (int)MathF.Floor(p.Y / BlockSize));

    /// <summary>Allocates every WorldCell up front, mirroring WorldManager engine parity.</summary>
    private static void AllocateCells(WorldTemplate template)
    {
        template.Cells = new WorldCell[template.CellX + 1, template.CellY + 1];
        for (var cy = 0; cy <= template.CellY; cy++)
        for (var cx = 0; cx <= template.CellX; cx++)
        {
            var cell = new WorldCell(cx, cy, template);
            typeof(WorldCell).GetProperty("Loaded", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
                .SetValue(cell, true);
            template.Cells[cx, cy] = cell;
        }
    }
    private static void SkipIfNoGameData()
    {
        if (!RigAvailable)
            throw new SkipTestException($"Real game data not available: {PakPath} missing or unmountable");
    }

    private static List<BaseBaiLoader> LoadBlockCluster((int X, int Y) center)
    {

        var loaded = new List<BaseBaiLoader>();
        for (var by = center.Y - 1; by <= center.Y + 1; by++)
        for (var bx = center.X - 1; bx <= center.X + 1; bx++)
        {
            var loader = new BaseBaiLoader(_template);
            try
            {
                loader.LoadBaiFilesFromFolder($"{bx:000}_{by:000}");
            }
            catch
            {
                continue; // mirrors engine: per-folder failures are non-fatal here
            }

            if (loader.NetMissionReaders.Count == 0 || loader.NetMissionReaders.All(r => r.NodeDescriptorList.IsEmpty))
                continue;

            var cellX = bx / 4;
            var cellY = by / 4;
            _template.Cells[cellX, cellY].BaiLoader[bx % 4, by % 4] = loader;
            _template.PathBaiLoader[((uint)bx, (uint)by)] = loader;
            loaded.Add(loader);
        }

        return loaded;
    }

    [Test]
    public async Task RealNetMissionBlocks_ParseWithLinkContinuity()
    {
        SkipIfNoGameData();

        long nodes = 0, links = 0, danglingLinks = 0;
        foreach (var loader in _solzreedLoaders)
        foreach (var reader in loader.NetMissionReaders)
        {
            nodes += reader.NodeDescriptorList.Count;
            links += reader.LinkDescriptorList.Count;
            foreach (var link in reader.LinkDescriptorList)
                if (link.SourceNodeDescriptor == null || link.TargetNodeDescriptor == null)
                    danglingLinks++;
        }

        await Assert.That(nodes).IsGreaterThan(1000);
        await Assert.That(links).IsGreaterThan(nodes); // triangulation graphs have more edges than nodes
        await Assert.That(danglingLinks).IsEqualTo(0);
    }

    [Test]
    public async Task SpatialIndex_MatchesLinearScan_OnRealNodes()
    {
        SkipIfNoGameData();

        var rng = new Random(20260825);
        var checkedPoints = 0;
        foreach (var loader in _solzreedLoaders)
        {
            var allNodes = new List<NodeDescriptor>();
            foreach (var reader in loader.NetMissionReaders)
            foreach (var (_, node) in reader.NodeDescriptorList)
                allNodes.Add(node);

            for (var sample = 0; sample < 64 && allNodes.Count > 0; sample++)
            {
                var anchor = allNodes[rng.Next(allNodes.Count)].Pos;
                var query = anchor + new Vector3(
                    (float)(rng.NextDouble() - 0.5) * 300f,
                    (float)(rng.NextDouble() - 0.5) * 300f,
                    (float)(rng.NextDouble() - 0.5) * 40f);

                // grid answer (engine path used by FindClosestToTheCurrent / GetNeighbours)
                var gridAnswer = loader.FindClosestNetMissionNode(query);
                if (gridAnswer == null)
                    throw new InvalidOperationException("grid nearest-node answer must not be null for a non-empty block");
                var gridDistance = Vector3.Distance(gridAnswer.Pos, query);

                // brute-force reference over every node of the block
                float linearDistance = float.MaxValue;
                NodeDescriptor linearAnswer = null;
                foreach (var reader in loader.NetMissionReaders)
                foreach (var (_, node) in reader.NodeDescriptorList)
                {
                    var d = Vector3.Distance(node.Pos, query);
                    if (d < linearDistance)
                    {
                        linearDistance = d;
                        linearAnswer = node;
                    }
                }

                if (linearAnswer == null)
                    throw new InvalidOperationException("linear nearest-node answer must not be null");
                // exact-minimum agreement (tiny tolerance for float reassociation)
                await Assert.That(MathF.Abs(gridDistance - linearDistance)).IsLessThanOrEqualTo(0.001f * MathF.Max(1f, linearDistance));
                checkedPoints++;
            }
        }

        await Assert.That(checkedPoints).IsGreaterThan(100);
    }

    [Test]
    public async Task SpatialIndex_VertexGrid_MatchesLinearScan()
    {
        SkipIfNoGameData();

        var withVertices = _solzreedLoaders.FirstOrDefault(l => l.VertexMissionReaders.Any(v => v.ObstacleDataDescriptorList.Count > 0));
        if (withVertices == null)
            return; // cluster has no obstacle vertices; nothing to verify

        var obstaclePoints = new List<Vector3>();
        foreach (var vertexMission in withVertices.VertexMissionReaders)
        foreach (var obstacleDataDescriptor in vertexMission.ObstacleDataDescriptorList)
            obstaclePoints.Add(obstacleDataDescriptor.Pos);

        var rng = new Random(7);
        for (var sample = 0; sample < 32; sample++)
        {
            var anchor = obstaclePoints[rng.Next(obstaclePoints.Count)];
            var query = anchor + new Vector3(
                (float)(rng.NextDouble() - 0.5) * 200f,
                (float)(rng.NextDouble() - 0.5) * 200f,
                (float)(rng.NextDouble() - 0.5) * 30f);

            var gridPoint = withVertices.FindClosestVertexPoint(query, out var gridDistance);
            float linearDistance = float.MaxValue;
            foreach (var point in obstaclePoints)
                linearDistance = MathF.Min(linearDistance, Vector3.Distance(point, query));

            await Assert.That(Vector3.Distance(gridPoint, query)).IsEqualTo(gridDistance);
            await Assert.That(MathF.Abs(gridDistance - linearDistance)).IsLessThanOrEqualTo(0.001f * MathF.Max(1f, linearDistance));
        }
    }

    /// <summary>
    /// Synthetic weighted graph: two parallel routes between S and G — one long hop and
    /// a chain of short hops with a lower total length. With the corrected G-cost
    /// (accumulated walked distance), A* must return the cheap chain; the old
    /// distance-to-goal G-cost broke this cost model.
    /// </summary>
    [Test]
    public async Task FindPath_GCostAccumulatesAlongWalkedPath_PrefersCheapRoute()
    {
        var world = BuildSyntheticWorld(out var reader);

        // S -> A -> B -> G: gently bent chain, total ~122.1 m
        // S -> H -> G: central-hub decoy pointing straight at the goal, total ~134.6 m
        AddNode(reader, 1, new Vector3(100, 105, 50)); // S
        AddNode(reader, 2, new Vector3(130, 125, 50)); // A
        AddNode(reader, 3, new Vector3(160, 85, 50));  // B
        AddNode(reader, 4, new Vector3(190, 105, 50)); // G
        AddNode(reader, 5, new Vector3(145, 55, 50)); // H (decoy hub)
        AddLink(reader, 1, 2);
        AddLink(reader, 2, 3);
        AddLink(reader, 3, 4);
        AddLink(reader, 1, 5);
        AddLink(reader, 5, 4);

        var result = new PathNode().FindPath(world, new Vector3(100, 105, 50), new Vector3(190, 105, 50));

        await Assert.That(result).IsNotEmpty();

        var length = 0f;
        for (var i = 1; i < result.Count; i++)
            length += Vector3.Distance(result[i - 1], result[i]);

        // The reduced path must be the cheap chain (~122.1 m incl. snap-in legs),
        // not the expensive decoy route (~134.6 m).
        await Assert.That(length).IsLessThan(128f);

        // The bend at A or B must survive Douglas-Peucker (tolerance 2 m): a straight
        // S→G reduction would mean the route ignored graph geometry.
        await Assert.That(result.Count).IsGreaterThanOrEqualTo(3);
    }

    [Test]
    public async Task FindPath_IsDeterministic_OnRealData()
    {
        SkipIfNoGameData();

        var start = new Vector3(SolzreedShore.X, SolzreedShore.Y, 50f);
        var goal = new Vector3(SolzreedShore.X - 180f, SolzreedShore.Y + 140f, 50f);

        var first = new PathNode().FindPath(_world, start, goal);
        var second = new PathNode().FindPath(_world, start, goal);

        await Assert.That(first).IsNotEmpty();
        await Assert.That(second.Count).IsEqualTo(first.Count);
        for (var i = 0; i < first.Count; i++)
            await Assert.That(second[i]).IsEqualTo(first[i]);
    }

    [Test]
    public async Task FindPath_ReachesGoals_OnlyWhenBfsConnectsThem()
    {
        SkipIfNoGameData();

        var start = new Vector3(SolzreedShore.X, SolzreedShore.Y, 50f);
        var reachableGoal = new Vector3(SolzreedShore.X - 180f, SolzreedShore.Y + 140f, 50f);
        var farAwayGoal = new Vector3(Marianople.X, Marianople.Y, 50f); // outside loaded cluster

        var bfsDistances = BfsFrom(start);
        await Assert.That(bfsDistances).IsNotNull();

        var snappedReachable = Snap(reachableGoal);
        await Assert.That(snappedReachable).IsNotNull();
        await Assert.That(bfsDistances.ContainsKey(snappedReachable.Value)).IsTrue();

        var found = new PathNode().FindPath(_world, start, reachableGoal);
        await Assert.That(found).IsNotEmpty(); // BFS-reachable ⇒ A* plans

        var beyondGraph = new PathNode().FindPath(_world, start, farAwayGoal);
        var snappedFar = Snap(farAwayGoal);
        var bfsSaysReachable = snappedFar.HasValue && bfsDistances.ContainsKey(snappedFar.Value);
        if (!bfsSaysReachable)
            await Assert.That(beyondGraph).IsEmpty(); // BFS-disconnected ⇒ A* must not fabricate a path
    }

    // --- synthetic-graph helpers -----------------------------------------------------------

    /// <summary>Builds a minimal headless world whose only nav data is the returned synthetic reader.</summary>
    private static WorldInstance BuildSyntheticWorld(out NetMissionReader reader)
    {
        var template = new WorldTemplate
        {
            Id = 0,
            Name = "synthetic_nav",
            CellX = 1,
            CellY = 1,
            MaxHeight = 1024f,
            HeightMaxCoefficient = 1f,
        };
        AllocateCells(template);
        template.GeoData = new AiGeoDataManager(template);

        reader = new NetMissionReader(new MemoryStream(), 0);
        var loader = new BaseBaiLoader(template);
        loader.NetMissionReaders.Add(reader);
        template.Cells[0, 0].BaiLoader[0, 0] = loader;
        template.PathBaiLoader[(0, 0)] = loader;

        return new WorldInstance(template, 0, true, 1);
    }

    private static void AddNode(NetMissionReader reader, int id, Vector3 pos)
    {
        reader.NodeDescriptorList[id] = new NodeDescriptor(reader) { Id = id, Pos = pos };
    }

    private static void AddLink(NetMissionReader reader, int sourceId, int targetId)
    {
        reader.LinkDescriptorList.Add(new LinkDescriptor(reader)
        {
            SourceNode = (uint)sourceId,
            TargetNode = (uint)targetId,
            SourceNodeDescriptor = reader.NodeDescriptorList.GetValueOrDefault(sourceId),
            TargetNodeDescriptor = reader.NodeDescriptorList.GetValueOrDefault(targetId),
        });
    }

    // --- BFS helpers over the real subgraph -------------------------------------------------

    private static NetMissionReader PrimaryReader(BaseBaiLoader loader)
        => loader?.NetMissionReaders.FirstOrDefault(r => !r.NodeDescriptorList.IsEmpty);

    private static Vector3? Snap(Vector3 pos)
    {
        var pathsPos = pos.ToPathsIndex();
        var loader = _template.PathBaiLoader.GetValueOrDefault(((uint)pathsPos.Item1, (uint)pathsPos.Item2));
        var nearest = loader?.FindClosestNetMissionNode(pos);
        return nearest?.Pos;
    }

    /// <summary>BFS over reader links of the loaded cluster, forbidden-area filtered like GetNeighbours.</summary>
    private static Dictionary<Vector3, int> BfsFrom(Vector3 start)
    {
        var origin = Snap(start);
        if (origin == null)
            return null;

        var distances = new Dictionary<Vector3, int> { [origin.Value] = 0 };
        var queue = new Queue<Vector3>();
        queue.Enqueue(origin.Value);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            var nexts = NeighboursOf(current);
            foreach (var neighbour in nexts)
            {
                if (distances.TryAdd(neighbour, distances[current] + 1))
                    queue.Enqueue(neighbour);
            }
        }

        return distances;
    }

    private static IEnumerable<Vector3> NeighboursOf(Vector3 pos)
    {
        var pathsPos = pos.ToPathsIndex();
        var loader = _template.PathBaiLoader.GetValueOrDefault(((uint)pathsPos.Item1, (uint)pathsPos.Item2));
        var reader = PrimaryReader(loader);
        if (reader == null)
            yield break;

        var nearest = loader.FindClosestNetMissionNode(pos);
        if (nearest == null)
            yield break;

        foreach (var link in reader.LinkDescriptorList)
        {
            if (link.SourceNode != nearest.Id)
                continue;
            var target = link.TargetNodeDescriptor;
            if (target == null)
                continue;
            if (_template.GeoData.CheckImpossibleWalk(target.Pos))
                continue;
            yield return target.Pos;
        }
    }
}
