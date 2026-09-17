using System.Numerics;
using System.Reflection;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.CryEngine.Entities;
using AAEmu.Game.Models.CryEngine.Loaders;
using AAEmu.Game.Models.CryEngine.Readers;
using AAEmu.Game.Models.Game.Indun;
using AAEmu.Game.Models.Game.World;

namespace AAEmu.UnitTests.Game.Models.Game.Indun;

/// <summary>
/// Pins the system-instance entry Z snap: template spawn positions can hang
/// over water/void (mirage /teleport entry at 3680,4572,Z156), which used to
/// drop players mid-air into fall damage. The entry path now snaps Z through
/// the same GeoData.GetHeight seam as the DuelManager duel-flag precedent.
/// Fully synthetic — no game_pak needed, never skips.
/// </summary>
public class DungeonEntrySnapTests
{
    private const float SpawnX = 3680f;
    private const float SpawnY = 4572f;
    private const float SpawnZ = 156f;
    private const float GroundZ = 149.5f;

    private static WorldTemplate BlankTemplate()
    {
        var template = new WorldTemplate
        {
            Id = 0,
            Name = "synthetic_entry_snap",
            OceanLevel = 100f,
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

    private static WorldTemplate TemplateWithGroundAtSpawn(float groundZ)
    {
        var template = BlankTemplate();
        var loader = new BaseBaiLoader(template);
        var reader = new NetMissionReader(new MemoryStream(), 0);
        reader.NodeDescriptorList[1] = new NodeDescriptor(reader) { Id = 1, Pos = new Vector3(SpawnX, SpawnY, groundZ) };
        loader.NetMissionReaders.Add(reader);
        template.ZoneBaiLoader[7] = loader;
        template.GeoData = new AiGeoDataManager(template);
        return template;
    }

    [Test]
    public async Task EntryZSnapsToTerrainHeight()
    {
        var template = TemplateWithGroundAtSpawn(GroundZ);

        var snapped = Dungeon.SnapEntryHeightToTerrain(template, new Vector3(SpawnX, SpawnY, SpawnZ), SpawnZ);

        await Assert.That(snapped).IsEqualTo(GroundZ);
    }

    [Test]
    public async Task NullTemplate_KeepsSpawnZ()
    {
        var snapped = Dungeon.SnapEntryHeightToTerrain(null, new Vector3(SpawnX, SpawnY, SpawnZ), SpawnZ);

        await Assert.That(snapped).IsEqualTo(SpawnZ);
    }

    [Test]
    public async Task NullGeoData_KeepsSpawnZ()
    {
        var template = BlankTemplate();
        template.GeoData = null;

        var snapped = Dungeon.SnapEntryHeightToTerrain(template, new Vector3(SpawnX, SpawnY, SpawnZ), SpawnZ);

        await Assert.That(snapped).IsEqualTo(SpawnZ);
    }

    [Test]
    public async Task ZeroSample_SnapsToWaterSurface()
    {
        // A zero nav sample is "no data" (open water/void), not the ground —
        // over water the entry floats to the surface instead of keeping a
        // mid-air template Z or dropping into the void.
        var template = TemplateWithGroundAtSpawn(0f);

        var snapped = Dungeon.SnapEntryHeightToTerrain(template, new Vector3(SpawnX, SpawnY, SpawnZ), SpawnZ);

        await Assert.That(snapped).IsEqualTo(100f);
    }

    [Test]
    public async Task DeepSeabed_SnapsToWaterSurface()
    {
        // Mirage entry over water: a positive-but-deep seabed (60 < surface
        // 100) must not become an underwater spawn.
        var template = TemplateWithGroundAtSpawn(60f);

        var snapped = Dungeon.SnapEntryHeightToTerrain(template, new Vector3(SpawnX, SpawnY, SpawnZ), SpawnZ);

        await Assert.That(snapped).IsEqualTo(100f);
    }

    [Test]
    public async Task NoWaterLevel_KeepsFallback()
    {
        // No water level on the template: keep the pre-hardening behavior
        // (ground snap only, otherwise the template Z).
        var template = BlankTemplate();
        template.OceanLevel = 0f;
        template.GeoData = new AiGeoDataManager(template);

        var snapped = Dungeon.SnapEntryHeightToTerrain(template, new Vector3(SpawnX, SpawnY, SpawnZ), SpawnZ);

        await Assert.That(snapped).IsEqualTo(SpawnZ);
    }
}
