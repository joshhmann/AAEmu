using System.Numerics;

using AAEmu.Commons.Utils;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.Housing;
using AAEmu.Game.Models.Game.World.Transform;
using AAEmu.Game.Models.Game.World.Zones;

using Microsoft.Data.Sqlite;

namespace AAEmu.UnitTests.Game.Models.Game.Housing;

/// <summary>
/// FIX-2 (t_9682e86a) — polygon-level placement validation: point-in-polygon containment
/// against pak housing_area.xml AreaShapes (joined via housing_areas.comments), per-shape
/// group rules (category / houseless / max_construct_count), unit overlap (114) and the
/// terrain band (115/116). Drives the REAL validator with 1.2-shaped shapes + rules.
/// </summary>
public class HousingPolygonPlacementTests
{
    private const uint GroupGeneral = 1;      // 일반 주거 지역 — all houses + farms
    private const uint GroupMediumHouse = 15; // 중형 주택 지역 — medium houses + thatched + pumpkin
    private const uint GroupHouseless = 12;   // 무주택자 전용 — homeless-only

    private static HousingTemplate House(uint id, uint category, float gardenRadius = 7.5f) => new()
    {
        Id = id, Name = $"house_{id}", CategoryId = category, GardenRadius = gardenRadius,
        // Canonical 1.2 terrain band (compact.sqlite3): above 0, below 10.
        ExtraHeightAbove = 0f, ExtraHeightBelow = 10f, HousingBindingDoodad = []
    };

    /// <summary>1.2-shaped guild hall (category 8 — the only category with a nonzero max in the data).</summary>
    private static HousingTemplate GuildHall() => House(101, 8, 10f);

    /// <summary>Square plot from (0,0) to (20,20) — canonical shape name style.</summary>
    private static Area Square(string name, float x0 = 0f, float y0 = 0f, float size = 20f) => new()
    {
        Id = 1,
        Name = name,
        Points =
        [
            new Vector3(x0, y0, 0),
            new Vector3(x0 + size, y0, 0),
            new Vector3(x0 + size, y0 + size, 0),
            new Vector3(x0, y0 + size, 0)
        ]
    };

    private static HousingAreaRule Rule(string shapeName, uint groupId, params (uint Category, int Max)[] categories) => new()
    {
        ShapeName = shapeName,
        HousingGroupId = groupId,
        AllowedCategories = categories.Select(c => c.Category).ToHashSet(),
        MaxConstructCounts = categories.ToDictionary(c => c.Category, c => c.Max),
        HouselessOnly = groupId == GroupHouseless
    };

    private static Dictionary<string, HousingAreaRule> RuleMap(params (string Shape, HousingAreaRule Rule)[] entries)
        => entries.ToDictionary(e => e.Shape, e => e.Rule);

    private static HousingPlacementError Validate(
        List<Area> shapes,
        Dictionary<string, HousingAreaRule> rules,
        HousingTemplate template,
        Vector3 position,
        bool ownsHouses = false,
        float? groundHeight = null,
        List<Vector3> units = null,
        List<House> existingHouses = null)
        => HousingPlacementValidator.ValidatePolygonPlacement(
            shapes,
            name => rules.GetValueOrDefault(name),
            template,
            position,
            ownsHouses,
            groundHeight,
            units ?? [],
            existingHouses ?? []);

    // ---------------------------------------------------------------- containment

    [Test]
    public async Task Position_InsideShape_ReturnsNone()
    {
        var shape = Square("LevelDesignShape_142_anne_2");
        var rule = Rule("LevelDesignShape_142_anne_2", GroupGeneral, (1, 0));
        var error = Validate([shape], RuleMap(("LevelDesignShape_142_anne_2", rule)),
            House(110, 1), new Vector3(10, 10, 5));
        await Assert.That(error).IsEqualTo(HousingPlacementError.None);
    }

    [Test]
    public async Task Position_OutsideEveryShape_ReturnsNoHousingArea()
    {
        var shape = Square("LevelDesignShape_142_anne_2");
        var rule = Rule("LevelDesignShape_142_anne_2", GroupGeneral, (1, 0));
        // Position at (50, 50) is inside the land zone but outside the polygon.
        var error = Validate([shape], RuleMap(("LevelDesignShape_142_anne_2", rule)),
            House(110, 1), new Vector3(50, 50, 5));
        await Assert.That(error).IsEqualTo(HousingPlacementError.NoHousingArea);
    }

    [Test]
    public async Task NoShapesLoaded_PolygonLayerSkipped_ReturnsNone()
    {
        // Client pak unavailable (no AreaShapes for the zone) — the zone-level checks still
        // gate, but the polygon layer cannot run and must not reject.
        var error = Validate([], RuleMap(),
            House(110, 1), new Vector3(10, 10, 5));
        await Assert.That(error).IsEqualTo(HousingPlacementError.None);
    }

    [Test]
    public async Task ShapeWithoutHousingAreasRow_ReturnsNoHousingArea()
    {
        // A pak shape with no matching housing_areas row (deleted/legacy shape) grants nothing.
        var shape = Square("LevelDesignShape_142_deleted_1");
        var error = Validate([shape], RuleMap(),
            House(110, 1), new Vector3(10, 10, 5));
        await Assert.That(error).IsEqualTo(HousingPlacementError.NoHousingArea);
    }

    // ---------------------------------------------------------------- per-shape group rules

    [Test]
    public async Task CategoryNotAllowedByShapeGroup_ReturnsInvalidArea()
    {
        // Group 15 (medium-house zone) allows 18/10/17 — a category-1 small house is rejected
        // even though the ZONE-level union might allow it.
        var shape = Square("LevelDesignShape_142_moang_3");
        var rule = Rule("LevelDesignShape_142_moang_3", GroupMediumHouse, (10, 0), (17, 0), (18, 0));
        var error = Validate([shape], RuleMap(("LevelDesignShape_142_moang_3", rule)),
            House(110, 1), new Vector3(10, 10, 5));
        await Assert.That(error).IsEqualTo(HousingPlacementError.InvalidArea);
    }

    [Test]
    public async Task CategoryAllowedByShapeGroup_ReturnsNone()
    {
        var shape = Square("LevelDesignShape_142_moang_3");
        var rule = Rule("LevelDesignShape_142_moang_3", GroupMediumHouse, (10, 0), (17, 0), (18, 0));
        var error = Validate([shape], RuleMap(("LevelDesignShape_142_moang_3", rule)),
            House(140, 10), new Vector3(10, 10, 5));
        await Assert.That(error).IsEqualTo(HousingPlacementError.None);
    }

    [Test]
    public async Task HouselessOnlyShape_OwnerWithHouse_ReturnsInvalidArea()
    {
        var shape = Square("LevelDesignShape_179_anne_6");
        var rule = Rule("LevelDesignShape_179_anne_6", GroupHouseless, (1, 0));
        var error = Validate([shape], RuleMap(("LevelDesignShape_179_anne_6", rule)),
            House(110, 1), new Vector3(10, 10, 5), ownsHouses: true);
        await Assert.That(error).IsEqualTo(HousingPlacementError.InvalidArea);
    }

    [Test]
    public async Task HouselessOnlyShape_HouselessOwner_ReturnsNone()
    {
        var shape = Square("LevelDesignShape_179_anne_6");
        var rule = Rule("LevelDesignShape_179_anne_6", GroupHouseless, (1, 0));
        var error = Validate([shape], RuleMap(("LevelDesignShape_179_anne_6", rule)),
            House(110, 1), new Vector3(10, 10, 5), ownsHouses: false);
        await Assert.That(error).IsEqualTo(HousingPlacementError.None);
    }

    [Test]
    public async Task OverlappingShapes_AnyAcceptingShape_Passes()
    {
        // Position inside two shapes: one rejects the category, the other accepts.
        var rejecting = Square("reject", 0, 0, 20);
        var accepting = Square("accept", 10, 10, 20); // overlaps (10..30) — position (15,15) in both
        var rules = RuleMap(
            ("reject", Rule("reject", GroupMediumHouse, (10, 0))),
            ("accept", Rule("accept", GroupGeneral, (1, 0))));
        var error = Validate([rejecting, accepting], rules,
            House(110, 1), new Vector3(15, 15, 5));
        await Assert.That(error).IsEqualTo(HousingPlacementError.None);
    }

    // ---------------------------------------------------------------- max_construct_count

    [Test]
    public async Task MaxConstructCount_Reached_ReturnsMaxConstructCount()
    {
        // Group 1 allows category 8 with max 3 (the canonical guild-hall cap).
        var shape = Square("LevelDesignShape_142_anne_2");
        var rule = Rule("LevelDesignShape_142_anne_2", GroupGeneral, (1, 0), (8, 3));
        var existing = new List<House>
        {
            HouseAt(501, 101, 3, 3),   // inside the square, same category
            HouseAt(502, 101, 3, 17),  // inside, same category
            HouseAt(503, 101, 17, 3)   // inside, same category
        };
        var error = Validate([shape], RuleMap(("LevelDesignShape_142_anne_2", rule)),
            GuildHall(), new Vector3(17, 17, 5), existingHouses: existing);
        await Assert.That(error).IsEqualTo(HousingPlacementError.MaxConstructCount);
    }

    [Test]
    public async Task MaxConstructCount_BelowCap_ReturnsNone()
    {
        var shape = Square("LevelDesignShape_142_anne_2");
        var rule = Rule("LevelDesignShape_142_anne_2", GroupGeneral, (1, 0), (8, 3));
        var existing = new List<House> { HouseAt(501, 101, 3, 3) };
        var error = Validate([shape], RuleMap(("LevelDesignShape_142_anne_2", rule)),
            GuildHall(), new Vector3(17, 17, 5), existingHouses: existing);
        await Assert.That(error).IsEqualTo(HousingPlacementError.None);
    }

    [Test]
    public async Task MaxConstructCount_HousesOutsideShape_DoNotCount()
    {
        var shape = Square("LevelDesignShape_142_anne_2");
        var rule = Rule("LevelDesignShape_142_anne_2", GroupGeneral, (1, 0), (8, 3));
        // Three same-category houses at (50,50) — outside the polygon — must not consume the cap.
        var existing = new List<House>
        {
            HouseAt(501, 101, 50, 50),
            HouseAt(502, 101, 60, 60),
            HouseAt(503, 101, 70, 70)
        };
        var error = Validate([shape], RuleMap(("LevelDesignShape_142_anne_2", rule)),
            GuildHall(), new Vector3(10, 10, 5), existingHouses: existing);
        await Assert.That(error).IsEqualTo(HousingPlacementError.None);
    }

    [Test]
    public async Task MaxConstructCount_UnlimitedZero_ReturnsNone()
    {
        var shape = Square("LevelDesignShape_142_anne_2");
        var rule = Rule("LevelDesignShape_142_anne_2", GroupGeneral, (1, 0), (8, 0)); // 0 = unlimited
        var existing = new List<House>
        {
            HouseAt(501, 101, 3, 3),
            HouseAt(502, 101, 3, 17),
            HouseAt(503, 101, 17, 3),
            HouseAt(504, 101, 17, 17)
        };
        var error = Validate([shape], RuleMap(("LevelDesignShape_142_anne_2", rule)),
            GuildHall(), new Vector3(10, 5, 5), existingHouses: existing);
        await Assert.That(error).IsEqualTo(HousingPlacementError.None);
    }

    [Test]
    public async Task MaxConstructCount_DifferentCategory_DoesNotCount()
    {
        var shape = Square("LevelDesignShape_142_anne_2");
        var rule = Rule("LevelDesignShape_142_anne_2", GroupGeneral, (1, 0), (8, 1));
        // Existing houses are category 1 — the category-8 cap must ignore them.
        var existing = new List<House> { HouseAt(501, 110, 3, 3), HouseAt(502, 110, 3, 17) };
        var error = Validate([shape], RuleMap(("LevelDesignShape_142_anne_2", rule)),
            GuildHall(), new Vector3(17, 17, 5), existingHouses: existing);
        await Assert.That(error).IsEqualTo(HousingPlacementError.None);
    }

    // ---------------------------------------------------------------- unit overlap (114)

    [Test]
    public async Task UnitInsideGardenRadius_ReturnsOverlapUnit()
    {
        var shape = Square("LevelDesignShape_142_anne_2");
        var rule = Rule("LevelDesignShape_142_anne_2", GroupGeneral, (1, 0));
        var error = Validate([shape], RuleMap(("LevelDesignShape_142_anne_2", rule)),
            House(110, 1), new Vector3(10, 10, 5),
            units: [new Vector3(12, 10, 5)]); // 2 m < garden radius 7.5
        await Assert.That(error).IsEqualTo(HousingPlacementError.OverlapUnit);
    }

    [Test]
    public async Task UnitOutsideGardenRadius_ReturnsNone()
    {
        var shape = Square("LevelDesignShape_142_anne_2");
        var rule = Rule("LevelDesignShape_142_anne_2", GroupGeneral, (1, 0));
        var error = Validate([shape], RuleMap(("LevelDesignShape_142_anne_2", rule)),
            House(110, 1), new Vector3(10, 10, 5),
            units: [new Vector3(25, 10, 5)]); // 15 m > garden radius 7.5
        await Assert.That(error).IsEqualTo(HousingPlacementError.None);
    }

    [Test]
    public async Task NoUnitsNearby_ReturnsNone()
    {
        var shape = Square("LevelDesignShape_142_anne_2");
        var rule = Rule("LevelDesignShape_142_anne_2", GroupGeneral, (1, 0));
        var error = Validate([shape], RuleMap(("LevelDesignShape_142_anne_2", rule)),
            House(110, 1), new Vector3(10, 10, 5));
        await Assert.That(error).IsEqualTo(HousingPlacementError.None);
    }

    // ---------------------------------------------------------------- terrain band (115/116)

    [Test]
    public async Task GroundTooHigh_ReturnsTerrainTooHigh()
    {
        // extra_height_above = 0 (canonical): ground must not rise above the placement base.
        var shape = Square("LevelDesignShape_142_anne_2");
        var rule = Rule("LevelDesignShape_142_anne_2", GroupGeneral, (1, 0));
        var template = House(110, 1);
        var error = Validate([shape], RuleMap(("LevelDesignShape_142_anne_2", rule)),
            template, new Vector3(10, 10, 5), groundHeight: 8f); // ground 3 m above base
        await Assert.That(error).IsEqualTo(HousingPlacementError.TerrainTooHigh);
    }

    [Test]
    public async Task GroundTooLow_ReturnsTerrainTooLow()
    {
        // extra_height_below = 10 (canonical): placement base may float up to 10 m.
        var shape = Square("LevelDesignShape_142_anne_2");
        var rule = Rule("LevelDesignShape_142_anne_2", GroupGeneral, (1, 0));
        var template = House(110, 1);
        var error = Validate([shape], RuleMap(("LevelDesignShape_142_anne_2", rule)),
            template, new Vector3(10, 10, 30), groundHeight: 5f); // 25 m above ground
        await Assert.That(error).IsEqualTo(HousingPlacementError.TerrainTooLow);
    }

    [Test]
    public async Task GroundWithinBand_ReturnsNone()
    {
        var shape = Square("LevelDesignShape_142_anne_2");
        var rule = Rule("LevelDesignShape_142_anne_2", GroupGeneral, (1, 0));
        var template = House(110, 1);
        var error = Validate([shape], RuleMap(("LevelDesignShape_142_anne_2", rule)),
            template, new Vector3(10, 10, 12), groundHeight: 5f); // 7 m above ground ≤ 10
        await Assert.That(error).IsEqualTo(HousingPlacementError.None);
    }

    [Test]
    public async Task NoHeightData_TerrainCheckSkipped_ReturnsNone()
    {
        var shape = Square("LevelDesignShape_142_anne_2");
        var rule = Rule("LevelDesignShape_142_anne_2", GroupGeneral, (1, 0));
        var template = House(110, 1);
        // groundHeight null (no geodata/heightmap configured) — the terrain band must not reject.
        var error = Validate([shape], RuleMap(("LevelDesignShape_142_anne_2", rule)),
            template, new Vector3(10, 10, 30), groundHeight: null);
        await Assert.That(error).IsEqualTo(HousingPlacementError.None);
    }

    // ---------------------------------------------------------------- real data (HousingGameData)

    [Test]
    public async Task HousingGameData_RealData_LoadsShapeRules()
    {
        var field = typeof(Singleton<HousingGameData>).GetField("s_instance", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        var previous = field?.GetValue(null);
        try
        {
            var gameData = new HousingGameData();
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var dbPath = new[]
            {
                Path.Combine(baseDir, "..", "..", "..", "..", "AAEmu.Game", "Data", "compact.sqlite3"),
                Path.Combine(Directory.GetCurrentDirectory(), "AAEmu.Game", "Data", "compact.sqlite3")
            }.First(File.Exists);

            using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
            connection.Open();
            gameData.Load(connection);
            field?.SetValue(null, gameData);

            // Canonical join: 401 housing_areas rows carry a comments key; all of them that
            // join a housing group become shape rules.
            await Assert.That(gameData.HousingAreaRuleCount).IsGreaterThanOrEqualTo(375);

            // A real Solzreed shape: LevelDesignShape_142_moang_3 → group 15 (medium-house zone).
            var rule = gameData.GetAreaRuleByShapeName("LevelDesignShape_142_moang_3");
            await Assert.That(rule).IsNotNull();
            await Assert.That(rule.HousingGroupId).IsEqualTo(GroupMediumHouse);
            await Assert.That(rule.HouselessOnly).IsFalse();

            // Group 1 (general residential) carries the canonical guild-hall cap 3 for category 8.
            var generalRule = gameData.GetAreaRuleByShapeName("LevelDesignShape_142_anne_2");
            if (generalRule != null)
            {
                await Assert.That(generalRule.MaxConstructCounts.TryGetValue(8, out var max)).IsTrue();
                await Assert.That(max).IsEqualTo(3);
            }
        }
        finally
        {
            field?.SetValue(null, previous);
        }
    }

    private static House HouseAt(uint id, uint templateId, float x, float y)
    {
        var house = new House
        {
            Id = id,
            Template = House(templateId, templateId == 101 ? 8u : 1u)
        };
        house.Transform = new Transform(house, null, new Vector3(x, y, 0), Vector3.Zero);
        return house;
    }
}
