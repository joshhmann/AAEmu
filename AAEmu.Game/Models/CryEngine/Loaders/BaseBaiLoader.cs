using System.IO;
using System.Numerics;
using AAEmu.Commons.Exceptions;
using NLog;
using AAEmu.Game.IO;
using AAEmu.Game.Models.CryEngine.Entities;
using AAEmu.Game.Models.CryEngine.Readers;
using AAEmu.Game.Models.Game.World;

namespace AAEmu.Game.Models.CryEngine.Loaders;

public class BaseBaiLoader(WorldTemplate parentWorldTemplate)
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();
    private WorldTemplate ParentWorldTemplate { get; } = parentWorldTemplate;
    public List<AreasMissionReader> AreasMissionReaders { get; } = [];
    public List<NetMissionReader> NetMissionReaders { get; } = [];
    private BaiPointGrid<NodeDescriptor> _netMissionGrid;
    private BaiPointGrid<Vector3> _vertexGrid;
    private readonly Lock _gridLock = new();

    public List<VertexMissionReader> VertexMissionReaders { get; } = [];
    public List<NetMissionReader> HideMissionReaders { get; } = [];

    /// <summary>
    /// Loads .bai files data from a given zone or path folder
    /// </summary>
    /// <param name="zoneOrPathsFolder"></param>
    /// <param name="additiveLoad"></param>
    /// <exception cref="GameException"></exception>
    public void LoadBaiFilesFromFolder(string zoneOrPathsFolder, bool additiveLoad = false)
    {
        var worldFolder = Path.Combine("game", "worlds", ParentWorldTemplate.Name);

        if (!additiveLoad)
            ClearData();

        Logger.Debug($"LoadBaiFilesFromFolder {zoneOrPathsFolder}");
        try
        {
            // AreasMission*.bai
            var areaFiles = GetFiles("areasmission*.bai", zoneOrPathsFolder);
            foreach (var areaFile in areaFiles)
            {
                // Try to get zone key from folder name
                var areaFolderName = Path.GetFileName(Path.GetDirectoryName(areaFile)) ?? "";

                if (string.IsNullOrWhiteSpace(areaFolderName))
                    continue;

                // Skip file if it doesn't exist anymore for whatever reason
                if (!ClientFileManager.FileExists(areaFile))
                    continue;

                //LabelLoading.Text = $"Areas: {fileIndex}/{areaFiles.Length}";
                //LabelLoading.Refresh();

                var (zoneKey, pathBlockX, pathBlockY) = GetZoneAndOffsetsByName(areaFolderName);
                var targetOffset = GetTargetOffsetByZoneOrPath(zoneKey, pathBlockX, pathBlockY);

                // Logger.Debug($"Areas File: {areaFile}");

                // Load all .bai files for data
                var fileStream = ClientFileManager.GetFileStream(areaFile);
                // Ignore files that are too small or null streams
                if (fileStream == null || fileStream.Length <= 20)
                {
                    fileStream?.Dispose();
                    continue;
                }

                try
                {
                    var area = new AreasMissionReader(fileStream, zoneKey);
                    area.ReaderPointOffset = targetOffset;
                    area.ReadFile();
                    AreasMissionReaders.Add(area);
                }
                catch (Exception ex)
                {
                    Logger.Debug($"Areas File Exception: {ex}, in {areaFile}, area offset {targetOffset}, skipping the rest of this file");
                }
                finally
                {
                    fileStream.Dispose();
                }
            }

            // NetMission*.bai
            var netFiles = GetFiles("netmission*.bai", zoneOrPathsFolder);
            foreach (var netFile in netFiles)
            {
                // Try to get zone key from folder name
                var netFolderName = Path.GetFileName(Path.GetDirectoryName(netFile)) ?? "";

                if (string.IsNullOrWhiteSpace(netFolderName))
                    continue;

                //LabelLoading.Text = $"Net: {fileIndex}/{netFiles.Length}";
                //LabelLoading.Refresh();

                var (zoneKey, pathBlockX, pathBlockY) = GetZoneAndOffsetsByName(netFolderName);
                var targetOffset = GetTargetOffsetByZoneOrPath(zoneKey, pathBlockX, pathBlockY);

                // Logger.Debug($"Net File: {netFile}");

                using var fs = ClientFileManager.GetFileStream(netFile);
                var net = new NetMissionReader(fs, zoneKey);
                try
                {
                    net.ReaderPointOffset = targetOffset;
                    net.ReadFile();
                    NetMissionReaders.Add(net);
                }
                catch (Exception ex)
                {
                    Logger.Debug($"Net File Exception: {ex}, in {netFile}");
                    // continue;
                }
            }

            // VertexMission*.bai
            var vertexFiles = GetFiles("vertsmission*.bai", zoneOrPathsFolder);
            foreach (var vertexFile in vertexFiles)
            {
                // Try to get zone key from folder name
                var vertexFolderName = Path.GetFileName(Path.GetDirectoryName(vertexFile)) ?? "";

                if (string.IsNullOrWhiteSpace(vertexFolderName))
                    continue;

                //LabelLoading.Text = $"Vertex: {fileIndex}/{vertexFiles.Length}";
                //LabelLoading.Refresh();

                var (zoneKey, pathBlockX, pathBlockY) = GetZoneAndOffsetsByName(vertexFolderName);
                var targetOffset = GetTargetOffsetByZoneOrPath(zoneKey, pathBlockX, pathBlockY);

                // Logger.Debug($"Vertex File: {vertexFile}");

                var fileStream = ClientFileManager.GetFileStream(vertexFile);
                if (fileStream == null)
                    continue;

                try
                {
                    var vertex = new VertexMissionReader(fileStream, zoneKey);
                    vertex.ReaderPointOffset = targetOffset;
                    vertex.ReadFile();
                    VertexMissionReaders.Add(vertex);
                }
                catch (Exception ex)
                {
                    Logger.Debug($"Vertex File Exception: {ex}, in {vertexFile}");
                }
                finally
                {
                    fileStream.Dispose();
                }
            }

            // HideMission*.bai
            var hideFiles = GetFiles("hidemission*.bai", zoneOrPathsFolder);
            foreach (var hideFile in hideFiles)
            {
                // Try to get zone key from folder name
                var hideFolderName = Path.GetFileName(Path.GetDirectoryName(hideFile)) ?? "";

                if (string.IsNullOrWhiteSpace(hideFolderName))
                    continue;

                //LabelLoading.Text = $"Hide: {fileIndex}/{hideFiles.Length}";
                //LabelLoading.Refresh();

                var (zoneKey, pathBlockX, pathBlockY) = GetZoneAndOffsetsByName(hideFolderName);
                var targetOffset = GetTargetOffsetByZoneOrPath(zoneKey, pathBlockX, pathBlockY);

                // Logger.Debug($"Hide File: {hideFile}");

                using var fs = ClientFileManager.GetFileStream(hideFile);
                var hide = new NetMissionReader(fs, zoneKey);
                try
                {
                    hide.ReaderPointOffset = targetOffset;
                    hide.ReadFile();
                    HideMissionReaders.Add(hide);
                }
                catch (Exception ex)
                {
                    Logger.Debug($"Hide File Exception: {ex}, in {hideFile}");
                    // continue;
                }
            }

            //LabelLoading.Text = "Done Loading .bai";
        }
        catch (Exception ex)
        {
            Logger.Error(ex.Message);
            throw new GameException($"Exception loading files from {zoneOrPathsFolder}: {ex.Message}");
        }

        return;

        // ZoneKey,PathX, PathY 
        (uint, uint, uint) GetZoneAndOffsetsByName(string folderName)
        {
            var pathBlockX = 0u;
            var pathBlockY = 0u;
            if (folderName.Contains("_"))
            {
                // This is a path folder, not a zone folder
                var sectorSplit = folderName.Split("_");
                if (sectorSplit.Length == 2)
                {
                    if (!uint.TryParse(sectorSplit[0], out pathBlockX))
                        pathBlockX = 0u;
                    if (!uint.TryParse(sectorSplit[1], out pathBlockY))
                        pathBlockY = 0u;
                }
            }

            if (!uint.TryParse(folderName, out var zoneKey))
                zoneKey = 0u;
            return (zoneKey, pathBlockX, pathBlockY);
        }

        string[] GetFiles(string searchPattern, string forZones)
        {
            var rootFolder = worldFolder;

            if (!string.IsNullOrWhiteSpace(forZones))
            {
                rootFolder = Path.Combine(rootFolder, forZones.Contains('_') ? "paths" : "zone", forZones);
            }

            return ClientFileManager.GetFilesInDirectory(rootFolder, searchPattern, true).ToArray();
        }

        Vector3 GetTargetOffsetByZoneOrPath(uint zoneKey, uint pathBlockX, uint pathBlockY)
        {
            if (zoneKey == 0 || !ParentWorldTemplate.XmlWorld.Zones.TryGetValue(zoneKey, out var xmlWorldZone))
                return new Vector3(pathBlockX * 256f, pathBlockY * 256f, 0f);
            return new Vector3(xmlWorldZone.OriginX * 1024f, xmlWorldZone.OriginY * 1024f, 0f);
        }
    }

    private void ClearData()
    {
        // New
        // AreasMissionReader.UsedAreaNames.Clear();
        AreasMissionReaders.Clear();
        NetMissionReaders.Clear();
        VertexMissionReaders.Clear();
        HideMissionReaders.Clear();
        lock (_gridLock)
        {
            Volatile.Write(ref _netMissionGrid, null);
            Volatile.Write(ref _vertexGrid, null);
        }
    }

    /// <summary>
    /// Lazily built exact nearest-node index over this loader's NetMission readers
    /// (one loaded 256 m path-block). Built once on first query; never for unloaded blocks.
    /// </summary>
    private BaiPointGrid<NodeDescriptor> NetMissionGrid
    {
        get
        {
            var grid = Volatile.Read(ref _netMissionGrid);
            if (grid != null)
                return grid;
            lock (_gridLock)
            {
                if (_netMissionGrid == null)
                {
                    var built = new BaiPointGrid<NodeDescriptor>(node => node.Pos);
                    foreach (var netMissionReader in NetMissionReaders)
                        foreach (var (_, nodeDescriptor) in netMissionReader.NodeDescriptorList)
                            built.Add(nodeDescriptor);
                    built.Build();
                    Volatile.Write(ref _netMissionGrid, built);
                }
                return _netMissionGrid;
            }
        }
    }

    /// <summary>Lazily built exact nearest-point index over this loader's VertexMission obstacle points.</summary>
    internal BaiPointGrid<Vector3> VertexGrid
    {
        get
        {
            var grid = Volatile.Read(ref _vertexGrid);
            if (grid != null)
                return grid;
            lock (_gridLock)
            {
                if (_vertexGrid == null)
                {
                    var built = new BaiPointGrid<Vector3>(pos => pos);
                    foreach (var vertexMission in VertexMissionReaders)
                        foreach (var obstacleDataDescriptor in vertexMission.ObstacleDataDescriptorList)
                            built.Add(obstacleDataDescriptor.Pos);
                    built.Build();
                    Volatile.Write(ref _vertexGrid, built);
                }
                return _vertexGrid;
            }
        }
    }

    /// <summary>
    /// Finds the nearest navigation node in this loader's block via the spatial grid
    /// (exact minimum, same result as the previous full linear scan).
    /// </summary>
    public NodeDescriptor FindClosestNetMissionNode(Vector3 pos)
    {
        return NetMissionGrid.FindNearest(pos, out _);
    }

    /// <summary>Finds the nearest obstacle vertex point in this loader's block via the spatial grid.</summary>
    internal Vector3 FindClosestVertexPoint(Vector3 pos, out float distance)
    {
        return VertexGrid.FindNearest(pos, out distance);
    }
}
