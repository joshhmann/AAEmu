using System.Numerics;
using System.Reflection;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.CryEngine.Entities;
using AAEmu.Game.Models.CryEngine.Loaders;
using AAEmu.Game.Models.CryEngine.Readers;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.NPChar;
namespace AAEmu.UnitTests.Game.Navigation;

/// <summary>
/// PB-005 Phase 1: contract of <see cref="AiGeoDataManager.TryGetNavSample"/> —
/// the navmesh-only sampler extracted verbatim from the legacy
/// <c>GetHeight</c> nearest node/vertex search. Each case pins the legacy
/// composition semantics: nearest-in-3D wins, node beats vertex on ties,
/// empty/missing data reports false (raw-heightmap fallback territory),
/// and the wrong-zone loader hazard surfaces with a huge planar distance
/// (which <c>NpcGroundingPolicy.NavMaxPlanarDistanceM</c> rejects).
/// Fully synthetic — no game_pak needed, never skips.
/// </summary>
public class AiGeoDataNavSampleTests
{
    // Nodes A(100,100,50) B(110,100,60); obstacle vertex V(104,100,55).
    private static readonly Vector3 NodeA = new(100f, 100f, 50f);
    private static readonly Vector3 NodeB = new(110f, 100f, 60f);
    private static readonly Vector3 VertexV = new(104f, 100f, 55f);

    private static WorldTemplate BlankTemplate()
    {
        var template = new WorldTemplate
        {
            Id = 0,
            Name = "synthetic_nav_sample",
            CellX = 1,
            CellY = 1,
            MaxHeight = 1024f,
            HeightMaxCoefficient = 1f,
        };
        template.Cells = new WorldCell[template.CellX + 1, template.CellY + 1];
        for (var cy = 0; cy <= template.CellY; cy++)
        for (var cx = 0; cx <= template.CellX; cx++)
        {
            var cell = new WorldCell(cx, cy, template);
            typeof(WorldCell).GetProperty("Loaded", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
                .SetValue(cell, true);
            template.Cells[cx, cy] = cell;
        }
        return template;
    }

    private static BaseBaiLoader LoaderWith(WorldTemplate template, bool withNodes, bool withVertices)
    {
        var loader = new BaseBaiLoader(template);
        if (withNodes)
        {
            var reader = new NetMissionReader(new MemoryStream(), 0);
            reader.NodeDescriptorList[1] = new NodeDescriptor(reader) { Id = 1, Pos = NodeA };
            reader.NodeDescriptorList[2] = new NodeDescriptor(reader) { Id = 2, Pos = NodeB };
            loader.NetMissionReaders.Add(reader);
        }
        if (withVertices)
        {
            var vertexReader = new VertexMissionReader(new MemoryStream(), 0);
            vertexReader.ObstacleDataDescriptorList.Add(new ObstacleDataDescriptor(0) { Pos = VertexV });
            loader.VertexMissionReaders.Add(vertexReader);
        }
        return loader;
    }

    [Test]
    public async Task NavCovered_NearestIn3DWins_NodeBeatsVertexOnTie()
    {
        var template = BlankTemplate();
        template.PathBaiLoader[(0, 0)] = LoaderWith(template, withNodes: true, withVertices: true);
        var geo = new AiGeoDataManager(template);

        // Q(103,100,52): dV=sqrt(10) < dA=sqrt(13) < dB=sqrt(113) → vertex V wins.
        await Assert.That(geo.TryGetNavSample(new Vector3(103f, 100f, 52f), out var navZ, out var planar)).IsTrue();
        await Assert.That(navZ).IsEqualTo(55f);
        await Assert.That(MathF.Abs(planar - 1f)).IsLessThan(0.001f);

        // Q=A exactly: node distance 0 — and a vertex AT the same point would
        // still lose the tie (strict < keeps the node).
        await Assert.That(geo.TryGetNavSample(NodeA, out var tieZ, out var tiePlanar)).IsTrue();
        await Assert.That(tieZ).IsEqualTo(50f);
        await Assert.That(tiePlanar).IsEqualTo(0f);
    }

    [Test]
    public async Task NoNavData_ReturnsFalse()
    {
        // No zone loaders, empty path grid: GetBaiByPos finds nothing —
        // the legacy GetHeight heightmap-fallback territory.
        var template = BlankTemplate();
        var geo = new AiGeoDataManager(template);

        await Assert.That(geo.TryGetNavSample(new Vector3(100f, 100f, 50f), out var navZ, out var planar)).IsFalse();
        await Assert.That(navZ).IsEqualTo(0f);
        await Assert.That(planar).IsEqualTo(float.MaxValue);
    }
    [Test]
    public async Task EmptyLoader_ReturnsFalse()
    {
        // A loader IS registered for the block but carries no readers —
        // both spatial grids are empty, same as the legacy scan finding nothing.
        var template = BlankTemplate();
        template.PathBaiLoader[(0, 0)] = new BaseBaiLoader(template);
        var geo = new AiGeoDataManager(template);

        await Assert.That(geo.TryGetNavSample(new Vector3(100f, 100f, 50f), out var navZ, out _)).IsFalse();
        await Assert.That(navZ).IsEqualTo(0f);
    }

    [Test]
    public async Task WrongZoneLoader_SurfacesFarSample_ForPlanarCap()
    {
        // The GetBaiByPos hazard: with ANY zone loader present it is returned
        // regardless of position. Nodes live at (5000,5000,300) — the query at
        // (100,100,50) still answers, with a planar distance far beyond the
        // 256 m policy cap (which is what makes this sample unusable).
        var template = BlankTemplate();
        var zoneLoader = new BaseBaiLoader(template);
        var reader = new NetMissionReader(new MemoryStream(), 0);
        reader.NodeDescriptorList[9] = new NodeDescriptor(reader) { Id = 9, Pos = new Vector3(5000f, 5000f, 300f) };
        zoneLoader.NetMissionReaders.Add(reader);
        template.ZoneBaiLoader[7] = zoneLoader;
        var geo = new AiGeoDataManager(template);

        await Assert.That(geo.TryGetNavSample(new Vector3(100f, 100f, 50f), out var navZ, out var planar)).IsTrue();
        await Assert.That(navZ).IsEqualTo(300f);
        await Assert.That(planar).IsGreaterThan(NpcGroundingPolicy.NavMaxPlanarDistanceM);
    }
}
