// Offline coarse height-grid baker for Solzreed (zone_groups id 5).
//
// Planning-use only: the baked Z values are ~18m coarse resamples and
// MUST NOT satisfy any measured-Z gate.
//
// Decoder reuse (exact game sources, compiled in via <Compile Include>):
//   - AAEmu.Game.Models.ClientData.Hmap.Read(BinaryReader, bool)
//   - AAEmu.Game.Models.ClientData.NodeCell.Read / GetHeight(ushort, ushort) / RawDataToHeight(uint)
// Cell->heightmap mapping mirrors:
//   - AAEmu.Game.Models.Game.World.WorldCell.LoadCellHeightMapFromClientData
//     (16x16 sectors per cell x 32x32 units per sector, nodes sorted by
//     BoxHeightmap.Min.X then BoxHeightmap.Min.Y), but keeps float heights
//     instead of HeightMaxCoefficient quantization.
// Pak access via AAEmu.Commons.Utils.AAPak.AAPak (read-only).

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using AAEmu.Commons.Utils.AAPak;
using AAEmu.Game.Models.ClientData;
using Microsoft.Data.Sqlite;

static class Program
{
    // World/heightmap geometry, mirroring AAEmu.Game world constants:
    // WorldManager.CELL_SIZE = 1024, REGION_SIZE = 64, SECTORS_PER_CELL = 16,
    // SECTOR_HMAP_RESOLUTION = 32, CELL_HMAP_RESOLUTION = 512 (2m samples).
    const int CellSize = 1024;
    const int SectorsPerCell = 16;
    const int SectorHmapResolution = 32;
    const int CellHmapResolution = SectorsPerCell * SectorHmapResolution; // 512

    const int Columns = 257;
    const int Rows = 155;

    static int Main(string[] args)
    {
        var pakPath = Arg(args, "--pak", "/root/aaemu-e2e/runtime/game-data/ClientData/game_pak");
        var dbPath = Arg(args, "--db", "/root/aaemu-e2e/runtime/game-data/Data/compact.sqlite3");
        var outDir = Arg(args, "--out", ".client_files/survey-grids");

        // 1. Bounds from compact.sqlite3 zone_groups id 5 (read-only).
        var (gx, gy, gw, gh, gname) = ReadZoneGroupBounds(dbPath);
        var minX = gx;
        var minY = gy;
        var maxX = gx + gw;
        var maxY = gy + gh;
        Console.WriteLine($"zone_groups id 5 ({gname}): x={gx} y={gy} w={gw} h={gh}");

        var dbMd5 = Md5OfFile(dbPath);
        Console.WriteLine($"compact.sqlite3 md5: {dbMd5}");

        // 2. Open game_pak read-only.
        var pak = new AAPak(pakPath, openAsReadOnly: true);
        if (!pak.isOpen)
        {
            Console.Error.WriteLine($"Failed to open game_pak: {pakPath}");
            return 2;
        }
        Console.WriteLine($"Opened game_pak with {pak.pakFiles.Count} files.");

        // 3. Discover heightmap.dat cells overlapping the rect, grouped by world.
        var cellRe = new Regex(@"^game/worlds/([^/]+)/cells/(\d+)_(\d+)/client/terrain/heightmap\.dat$",
            RegexOptions.IgnoreCase);
        var cx0 = (int)Math.Floor(minX / CellSize);
        var cx1 = (int)Math.Floor((maxX - 1e-3) / CellSize);
        var cy0 = (int)Math.Floor(minY / CellSize);
        var cy1 = (int)Math.Floor((maxY - 1e-3) / CellSize);
        Console.WriteLine($"Need cells X {cx0}..{cx1}, Y {cy0}..{cy1}.");

        var byWorld = new Dictionary<string, List<(int cx, int cy, string entry)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in pak.pakFiles.Keys)
        {
            var m = cellRe.Match(key);
            if (!m.Success)
                continue;
            var world = m.Groups[1].Value;
            var cx = int.Parse(m.Groups[2].Value);
            var cy = int.Parse(m.Groups[3].Value);
            if (cx < cx0 || cx > cx1 || cy < cy0 || cy > cy1)
                continue;
            if (!byWorld.TryGetValue(world, out var list))
                byWorld[world] = list = [];
            list.Add((cx, cy, key));
        }
        foreach (var (w, l) in byWorld.OrderBy(kv => kv.Key))
            Console.WriteLine($"  world {w}: {l.Count} overlapping cells.");
        if (byWorld.Count == 0)
        {
            Console.Error.WriteLine("No heightmap.dat cells found for the Solzreed rect.");
            return 3;
        }

        // 4. Decode the best-covering world (most cells present).
        var bestWorld = byWorld.OrderByDescending(kv => kv.Value.Count).First().Key;
        Console.WriteLine($"Using world: {bestWorld}");
        var cells = new Dictionary<(int cx, int cy), float[,]>();
        float oceanLevel = float.NaN;
        var decodedCells = new List<string>();
        foreach (var (cx, cy, entry) in byWorld[bestWorld].OrderBy(c => c.cx).ThenBy(c => c.cy))
        {
            var grid = DecodeCell(pak, entry, ref oceanLevel);
            if (grid is null)
            {
                Console.WriteLine($"  cell {cx:000}_{cy:000}: decode failed, treated as missing.");
                continue;
            }
            cells[(cx, cy)] = grid;
            decodedCells.Add($"{cx:000}_{cy:000}");
        }
        Console.WriteLine($"Decoded {cells.Count}/{byWorld[bestWorld].Count} cells. OceanWaterLevel={oceanLevel}");

        // 5. Sample the coarse grid (north-to-south rows, west-to-east columns).
        var spacingX = gw / (Columns - 1);
        var spacingY = gh / (Rows - 1);
        var data = new float[Rows * Columns];
        var finite = 0;
        double minZ = double.PositiveInfinity, maxZ = double.NegativeInfinity;
        for (var r = 0; r < Rows; r++)
        {
            var y = maxY - r * spacingY; // row 0 = north edge (max Y)
            for (var c = 0; c < Columns; c++)
            {
                var x = minX + c * spacingX; // col 0 = west edge (min X)
                var z = SampleBilinear(cells, (float)x, (float)y);
                data[r * Columns + c] = z;
                if (float.IsFinite(z))
                {
                    finite++;
                    if (z < minZ) minZ = z;
                    if (z > maxZ) maxZ = z;
                }
            }
        }
        var finiteFraction = (double)finite / data.Length;
        Console.WriteLine($"Grid {Columns}x{Rows}, finite {finite}/{data.Length} ({finiteFraction:P2}), minZ={minZ:F2} maxZ={maxZ:F2}");

        // 6. Verify before writing.
        if (finite == 0)
        {
            Console.Error.WriteLine("No finite samples; refusing to write.");
            return 4;
        }
        if (finiteFraction < 0.5)
        {
            Console.Error.WriteLine($"Finite fraction {finiteFraction:P2} below 50%; refusing to write.");
            return 5;
        }
        if (!(maxZ >= 50 && maxZ <= 1500 && minZ >= -500))
        {
            Console.Error.WriteLine($"Implausible min/max Z ({minZ:F2}/{maxZ:F2}); refusing to write.");
            return 6;
        }

        // 7. Write LE float32 grid + sha256.
        Directory.CreateDirectory(outDir);
        var gridPath = Path.Combine(outDir, "solzreed.f32");
        var bytes = new byte[data.Length * 4];
        Buffer.BlockCopy(data, 0, bytes, 0, bytes.Length);
        File.WriteAllBytes(gridPath, bytes);
        if (new FileInfo(gridPath).Length != Columns * Rows * 4)
        {
            Console.Error.WriteLine("Grid file size mismatch after write.");
            return 7;
        }
        var sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        // 8. Manifest with full provenance.
        var manifest = new Dictionary<string, object?>
        {
            ["grid"] = "solzreed.f32",
            ["zone_group"] = new Dictionary<string, object?>
            {
                ["id"] = 5, ["name"] = gname, ["x"] = gx, ["y"] = gy, ["w"] = gw, ["h"] = gh,
            },
            ["bounds"] = new Dictionary<string, object?>
            {
                ["min_x"] = minX, ["min_y"] = minY, ["max_x"] = maxX, ["max_y"] = maxY,
            },
            ["columns"] = Columns,
            ["rows"] = Rows,
            ["spacing_x"] = spacingX,
            ["spacing_y"] = spacingY,
            ["spacing_nominal"] = (spacingX + spacingY) / 2,
            ["row_order"] = "north-to-south (row 0 = max Y / north edge, row N = min Y / south edge)",
            ["column_order"] = "west-to-east (col 0 = min X)",
            ["min_z"] = minZ,
            ["max_z"] = maxZ,
            ["finite_count"] = finite,
            ["finite_fraction"] = finiteFraction,
            ["ocean_level"] = float.IsNaN(oceanLevel) ? null : oceanLevel,
            ["nodata_value"] = "NaN (IEEE 754 little-endian float32)",
            ["nodata_rule"] = "NaN where no heightmap.dat cell covers the sample (bilinear corners all missing); mean of finite corners otherwise",
            ["sha256"] = sha256,
            ["decoder"] = new Dictionary<string, object?>
            {
                ["class"] = "AAEmu.Game.Models.ClientData.Hmap",
                ["methods"] = new[]
                {
                    "Hmap.Read(BinaryReader, bool)",
                    "NodeCell.Read(BinaryReader, bool)",
                    "NodeCell.GetHeight(ushort, ushort)",
                    "NodeCell.RawDataToHeight(uint)",
                },
                ["source_files"] = new[]
                {
                    "AAEmu.Game/Models/ClientData/Hmap.cs",
                    "AAEmu.Game/Models/ClientData/NodeCell.cs",
                    "AAEmu.Game/Models/ClientData/AABB.cs",
                },
                ["linkage"] = "compiled into Tools/SurveyGridBaker via <Compile Include> (same symbols, no fork)",
                ["cell_mapping"] = "AAEmu.Game/Models/Game/World/WorldCell.LoadCellHeightMapFromClientData (16x16 sectors x 32x32 units, nodes sorted by BoxHeightmap Min.X/Min.Y; float heights kept, no HeightMaxCoefficient quantization)",
                ["pak_reader"] = "AAEmu.Commons.Utils.AAPak.AAPak (read-only)",
            },
            ["world"] = bestWorld,
            ["cells"] = decodedCells.OrderBy(s => s).ToArray(),
            ["pak_path"] = Path.GetFullPath(pakPath),
            ["database"] = new Dictionary<string, object?>
            {
                ["path"] = Path.GetFullPath(dbPath),
                ["md5"] = dbMd5,
                ["table"] = "zone_groups",
                ["id"] = 5,
            },
            ["git_head"] = GitHead(),
            ["tool"] = "Tools/SurveyGridBaker",
            ["planning_only"] = true,
            ["planning_note"] = "Baked Z is planning-only (coarse ~18m resample) and MUST NOT satisfy any measured-Z gate.",
            ["baked_utc"] = DateTime.UtcNow.ToString("o"),
        };
        var manifestPath = Path.Combine(outDir, "manifest.json");
        var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(manifestPath, json);
        Console.WriteLine($"Wrote {gridPath} ({bytes.Length} bytes, sha256 {sha256})");
        Console.WriteLine($"Wrote {manifestPath}");
        return 0;
    }

    static float[,] DecodeCell(AAPak pak, string entry, ref float oceanLevel)
    {
        try
        {
            using var stream = pak.ExportFileAsStream(entry);
            if (stream is null || stream.Length == 0)
                return null;
            using var br = new BinaryReader(stream);
            var hmap = new Hmap();
            if (hmap.Read(br, false) < 0)
                return null;
            if (float.IsNaN(oceanLevel))
                oceanLevel = hmap.OceanWaterLevel;

            var sortedNodes = hmap.Nodes
                .OrderBy(n => n.BoxHeightmap.Min.X)
                .ThenBy(n => n.BoxHeightmap.Min.Y)
                .Where(n => n.pHMData is { Length: > 0 })
                .ToList();

            var grid = new float[CellHmapResolution, CellHmapResolution];
            for (var i = 0; i < grid.Length; i++)
                grid[i / CellHmapResolution, i % CellHmapResolution] = float.NaN;

            for (var sectorX = 0; sectorX < SectorsPerCell; sectorX++)
                for (var sectorY = 0; sectorY < SectorsPerCell; sectorY++)
                {
                    var nodeIndex = sectorX * SectorsPerCell + sectorY;
                    if (nodeIndex >= sortedNodes.Count)
                        continue; // missing sector stays NaN
                    var node = sortedNodes[nodeIndex];
                    for (var unitX = 0; unitX < SectorHmapResolution; unitX++)
                        for (var unitY = 0; unitY < SectorHmapResolution; unitY++)
                        {
                            var oX = sectorX * SectorHmapResolution + unitX;
                            var oY = sectorY * SectorHmapResolution + unitY;
                            grid[oX, oY] = node.GetHeight((ushort)unitX, (ushort)unitY);
                        }
                }
            return grid;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  decode error for {entry}: {ex.Message}");
            return null;
        }
    }

    static float SampleBilinear(Dictionary<(int cx, int cy), float[,]> cells, float x, float y)
    {
        var cx = (int)Math.Floor(x / CellSize);
        var cy = (int)Math.Floor(y / CellSize);
        if (!cells.TryGetValue((cx, cy), out var grid))
            return float.NaN;
        // 2m sample spacing within the cell.
        var fx = (x - cx * CellSize) / 2f;
        var fy = (y - cy * CellSize) / 2f;
        var x0 = (int)Math.Floor(fx);
        var y0 = (int)Math.Floor(fy);
        var tx = fx - x0;
        var ty = fy - y0;
        var x1 = Math.Min(x0 + 1, CellHmapResolution - 1);
        var y1 = Math.Min(y0 + 1, CellHmapResolution - 1);
        x0 = Math.Clamp(x0, 0, CellHmapResolution - 1);
        y0 = Math.Clamp(y0, 0, CellHmapResolution - 1);
        var h00 = grid[x0, y0];
        var h10 = grid[x1, y0];
        var h01 = grid[x0, y1];
        var h11 = grid[x1, y1];
        double sum = 0, wsum = 0;
        if (float.IsFinite(h00)) { sum += h00 * (1 - tx) * (1 - ty); wsum += (1 - tx) * (1 - ty); }
        if (float.IsFinite(h10)) { sum += h10 * tx * (1 - ty); wsum += tx * (1 - ty); }
        if (float.IsFinite(h01)) { sum += h01 * (1 - tx) * ty; wsum += (1 - tx) * ty; }
        if (float.IsFinite(h11)) { sum += h11 * tx * ty; wsum += tx * ty; }
        return wsum > 0 ? (float)(sum / wsum) : float.NaN;
    }

    static (double x, double y, double w, double h, string name) ReadZoneGroupBounds(string dbPath)
    {
        using var con = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;");
        con.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT x, y, w, h, name FROM zone_groups WHERE id = 5;";
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            throw new InvalidOperationException("zone_groups id 5 not found in compact.sqlite3.");
        return (reader.GetDouble(0), reader.GetDouble(1), reader.GetDouble(2), reader.GetDouble(3), reader.GetString(4));
    }

    static string Md5OfFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(MD5.HashData(stream)).ToLowerInvariant();
    }

    static string GitHead()
    {
        try
        {
            var psi = new ProcessStartInfo("git", "rev-parse HEAD")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var proc = Process.Start(psi);
            if (proc is null)
                return "unknown";
            var head = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit(10000);
            return proc.ExitCode == 0 && head.Length == 40 ? head : "unknown";
        }
        catch
        {
            return "unknown";
        }
    }

    static string Arg(string[] args, string name, string @default)
    {
        for (var i = 0; i + 1 < args.Length; i++)
            if (args[i] == name)
                return args[i + 1];
        return @default;
    }
}
