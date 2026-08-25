// Offline corridor measurement rig — drives the REAL engine chain
// (ClientFileManager -> BaseBaiLoader -> NetMissionReader/AreasMissionReader ->
//  AiGeoDataManager -> PathNode.FindPath) headlessly against the game_pak.
// Read-only: the pak is mounted read-only; nothing is written to game data.
using System.Diagnostics;
using System.Numerics;
using System.Reflection;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.IO;
using AAEmu.Game.Models.Game.AI.AStar;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.CryEngine.Loaders;

const string PakPath = "/root/aaemu-e2e/runtime/game-data/ClientData/game_pak";

// T4 corridor anchors (Data/Portal/respawns.json)
var SolzreedShore = new Vector2(15232.6f, 15341.4f); // "Pirate Respawn: Solzreed"
var Marianople = new Vector2(11022.2f, 12207.8f);    // "Respawn: Marianople"

const int BlockSize = 256;
const int CellSize = 1024;

if (!ClientFileManager.AddSource(PakPath))
{
    Console.Error.WriteLine("FATAL: could not mount game_pak read-only");
    return 1;
}

var template = new WorldTemplate
{
    Id = 0,
    Name = "main_world",
    CellX = 31,
    CellY = 31,
    MaxHeight = 1024f,
    HeightMaxCoefficient = 1f,
};
template.Cells = new WorldCell[template.CellX + 1, template.CellY + 1];
template.GeoData = new AiGeoDataManager(template);
var world = new WorldInstance(template, 0, true, 1);

var loadedCells = new HashSet<(int, int)>();

// Registers a cell with its 16 per-block loaders, pre-marked as loaded so that
// WorldCell.VerifyCellLoaded() never touches AppConfiguration/heightmap IO here.
// Engine parity: all WorldCell objects exist up front (WorldManager allocates every
// cell at world creation); only their .bai payload is block-lazy. We mirror that by
// allocating every cell now and preloading exactly the T4 corridor rectangle.
for (var cy = 0; cy <= template.CellY; cy++)
for (var cx = 0; cx <= template.CellX; cx++)
{
    var cell = new WorldCell(cx, cy, template) { BaiLoader = new BaseBaiLoader[4, 4] };
    template.Cells[cx, cy] = cell;
    typeof(WorldCell).GetProperty("Loaded", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
        .SetValue(cell, true);
}

void LoadCorridor()
{
    // T4: blocks[41..61]x[45..61]; whole cells covering them
    for (var cx = 41 / 4; cx <= 61 / 4; cx++)
    for (var cy = 45 / 4; cy <= 61 / 4; cy++)
    for (var y = 0; y < 4; y++)
    for (var x = 0; x < 4; x++)
    {
        var bx = cx * 4 + x;
        var by = cy * 4 + y;
        if (bx < 41 || bx > 61 || by < 45 || by > 61)
            continue;
        var folder = $"{bx:000}_{by:000}";
        var loader = new BaseBaiLoader(template);
        try { loader.LoadBaiFilesFromFolder(folder); }
        catch (Exception ex) { Console.Error.WriteLine($"# block {folder}: load exception {ex.Message}"); }
        template.Cells[cx, cy].BaiLoader[x, y] = loader;
        template.PathBaiLoader[((uint)bx, (uint)by)] = loader;
    }
}


var swLoad = Stopwatch.StartNew();
LoadCorridor();
swLoad.Stop();

int blocks = template.PathBaiLoader.Count;
long nodes = 0, links = 0;
foreach (var loader in template.PathBaiLoader.Values)
    foreach (var r in loader.NetMissionReaders)
    {
        nodes += r.NodeDescriptorList.Count;
        links += r.LinkDescriptorList.Count;
    }
Console.WriteLine($"# loaded {blocks} blocks: nodes={nodes} links={links} in {swLoad.Elapsed.TotalSeconds:F1}s");

Vector3 V(Vector2 p, float z = 50f) => new(p.X, p.Y, z);

var offsets = new[] { 0f, -192f, 192f };
Console.WriteLine("startX\tstartY\tgoalX\tgoalY\toutcome\twaypoints\tpathLenM\tdirectM\tms\texpansions");

var pathfinder = new PathNode();
int goalReached = 0, emptyPaths = 0;
long totalMs = 0;
foreach (var dxS in offsets)
foreach (var dyS in offsets)
foreach (var dxG in offsets)
foreach (var dyG in offsets)
{
    var sp = V(new Vector2(SolzreedShore.X + dxS, SolzreedShore.Y + dyS));
    var gp = V(new Vector2(Marianople.X + dxG, Marianople.Y + dyG));


    // warm the two snap queries out of timing (they are cached nowhere, but keep
    // first-call JIT noise off plan 0 by doing one throwaway short plan first)
    var sw = Stopwatch.StartNew();
    var result = pathfinder.FindPath(world, sp, gp);
    sw.Stop();
    totalMs += sw.ElapsedMilliseconds;

    float pathLen = 0f;
    for (var i = 1; i < result.Count; i++)
        pathLen += Vector3.Distance(result[i - 1], result[i]);
    var direct = Vector3.Distance(sp, gp);
    if (result.Count > 0) goalReached++; else emptyPaths++;
    Console.WriteLine(
        $"{sp.X:F0}\t{sp.Y:F0}\t{gp.X:F0}\t{gp.Y:F0}\t{(result.Count > 0 ? "GoalReached" : "Empty")}\t{result.Count}\t{pathLen:F0}\t{direct:F0}\t{sw.ElapsedMilliseconds}\t{pathfinder.ExpandedNodesLastSearch}");
}

Console.WriteLine($"# SUMMARY: GoalReached={goalReached}/81 Empty={emptyPaths}/81 totalPlanMs={totalMs}");
return 0;
