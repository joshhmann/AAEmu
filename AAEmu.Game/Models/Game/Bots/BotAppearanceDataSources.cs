using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Utils.DB;

namespace AAEmu.Game.Models.Game.Bots;

/// <summary>
/// Supplies the VALID randomization catalogs for one character model id.
/// Every catalog is canonical 1.2 data (compact.sqlite3, npc_only='f') — an
/// id outside these lists is not something the 1.2 client renders.
/// </summary>
public interface IBotAppearanceColorSource
{
    /// <summary>Valid hair color ids (hair_colors) for the model.</summary>
    IReadOnlyList<uint> GetHairColorIds(uint modelId);

    /// <summary>Valid skin color ids (skin_colors) for the model.</summary>
    IReadOnlyList<uint> GetSkinColorIds(uint modelId);

    /// <summary>Valid fixed decal asset ids for a category (face_decal_assets, movable='f').</summary>
    IReadOnlyList<uint> GetFixedDecalIds(uint modelId, byte categoryId);

    /// <summary>Valid movable decal assets (face_decal_assets, movable='t') with their default X/Y.</summary>
    IReadOnlyList<BotMovableDecal> GetMovableDecals(uint modelId);

    /// <summary>Valid face normal map ids (face_normal_maps) for the model.</summary>
    IReadOnlyList<uint> GetNormalMapIds(uint modelId);

    /// <summary>
    /// Valid face morph presets (custom_face_presets) for the model — each a
    /// 128-byte modifier block the client accepts verbatim.
    /// </summary>
    IReadOnlyList<byte[]> GetFacePresetModifiers(uint modelId);
}

/// <summary>A movable face decal with its canonical placement.</summary>
public sealed record BotMovableDecal(uint AssetId, short DefaultX, short DefaultY);

/// <summary>
/// Supplies the starting-equipment data (the SAME data the human create path
/// reads: character_equip_packs/character_supplies + item_body_parts).
/// </summary>
public interface IBotStartingEquipmentSource
{
    /// <summary>Newbie gear pack + supplies for the ability (canonical, may be empty).</summary>
    AbilityItems GetAbilityEquipment(byte abilityId);

    /// <summary>The 7 body-item template ids for the race/gender (0 = none).</summary>
    uint[] GetBodyItems(Race race, Gender gender);
}

/// <summary>
/// Canonical 1.2 catalog provider — reads compact.sqlite3 (the same file the
/// template managers load at boot) once per model and caches. Any catalog
/// that comes back empty is a data gap: callers fall back to the
/// render-proven canonical default (BotAppearanceDefaults).
/// </summary>
public sealed class SqliteBotAppearanceColorSource : IBotAppearanceColorSource
{
    private sealed class ModelPalette
    {
        public uint[] Hair = [];
        public uint[] Skin = [];
        public uint[] NormalMaps = [];
        public List<byte[]> PresetModifiers = [];
        public Dictionary<byte, uint[]> FixedDecals = [];
        public BotMovableDecal[] MovableDecals = [];
    }

    private readonly Dictionary<uint, ModelPalette> _cache = [];

    public IReadOnlyList<uint> GetHairColorIds(uint modelId) => Palette(modelId).Hair;
    public IReadOnlyList<uint> GetSkinColorIds(uint modelId) => Palette(modelId).Skin;
    public IReadOnlyList<uint> GetNormalMapIds(uint modelId) => Palette(modelId).NormalMaps;
    public IReadOnlyList<byte[]> GetFacePresetModifiers(uint modelId) => Palette(modelId).PresetModifiers;

    public IReadOnlyList<uint> GetFixedDecalIds(uint modelId, byte categoryId)
        => Palette(modelId).FixedDecals.GetValueOrDefault(categoryId) ?? [];

    public IReadOnlyList<BotMovableDecal> GetMovableDecals(uint modelId)
        => Palette(modelId).MovableDecals;

    private ModelPalette Palette(uint modelId)
    {
        lock (_cache)
        {
            if (_cache.TryGetValue(modelId, out var palette))
                return palette;

            palette = Load(modelId);
            _cache[modelId] = palette;
            return palette;
        }
    }

    private static ModelPalette Load(uint modelId)
    {
        var palette = new ModelPalette();
        try
        {
            using var connection = SQLite.CreateConnection();
            palette.Hair = QueryUints(connection, "SELECT id FROM hair_colors WHERE model_id=@m AND npc_only='f'", modelId);
            palette.Skin = QueryUints(connection, "SELECT id FROM skin_colors WHERE model_id=@m AND npc_only='f'", modelId);
            palette.NormalMaps = QueryUints(connection, "SELECT id FROM face_normal_maps WHERE model_id=@m AND npc_only='f'", modelId);

            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT modifier FROM custom_face_presets WHERE model_id=@m";
                cmd.Parameters.AddWithValue("@m", modelId);
                cmd.Prepare();
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    if (reader.GetValue(0) is byte[] modifier && modifier.Length == 128)
                        palette.PresetModifiers.Add(modifier);
                }
            }

            foreach (var categoryId in new byte[] { 2, 3, 4, 5 })
            {
                var ids = QueryUints(connection,
                    "SELECT id FROM face_decal_assets WHERE model_id=@m AND npc_only='f' AND movable='f' AND category_id=@c",
                    modelId, ("@c", categoryId));
                if (ids.Length > 0)
                    palette.FixedDecals[categoryId] = ids;
            }

            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT id, defaultX, defaultY FROM face_decal_assets WHERE model_id=@m AND npc_only='f' AND movable='t'";
                cmd.Parameters.AddWithValue("@m", modelId);
                cmd.Prepare();
                using var reader = cmd.ExecuteReader();
                var list = new List<BotMovableDecal>();
                while (reader.Read())
                {
                    list.Add(new BotMovableDecal(
                        Convert.ToUInt32(reader.GetValue(0)),
                        Convert.ToInt16(reader.GetValue(1)),
                        Convert.ToInt16(reader.GetValue(2))));
                }
                palette.MovableDecals = list.ToArray();
            }
        }
        catch (Exception)
        {
            // Data file missing / unreadable (e.g. a bare test host): every
            // catalog stays empty and callers use the canonical defaults —
            // the appearance still renders (the P0-proven path).
        }

        return palette;
    }

    private static uint[] QueryUints(Microsoft.Data.Sqlite.SqliteConnection connection, string sql, uint modelId, params (string Name, object Value)[] extra)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@m", modelId);
        foreach (var (name, value) in extra)
            cmd.Parameters.AddWithValue(name, value);
        cmd.Prepare();
        using var reader = cmd.ExecuteReader();
        var ids = new List<uint>();
        while (reader.Read())
            ids.Add(Convert.ToUInt32(reader.GetValue(0)));
        return ids.ToArray();
    }
}

/// <summary>
/// Hermetic catalog provider for rigs — the test declares exactly what the
/// factory may pick from, so determinism/distinctness/validity assertions are
/// stable without a data file.
/// </summary>
public sealed class StaticBotAppearanceColorSource : IBotAppearanceColorSource
{
    private readonly Dictionary<uint, (uint[] Hair, uint[] Skin, uint[] NormalMaps, List<byte[]> Presets,
        Dictionary<byte, uint[]> FixedDecals, BotMovableDecal[] Movable)> _palettes;

    public StaticBotAppearanceColorSource(Dictionary<uint, (uint[] Hair, uint[] Skin, uint[] NormalMaps,
        List<byte[]> Presets, Dictionary<byte, uint[]> FixedDecals, BotMovableDecal[] Movable)> palettes)
    {
        _palettes = palettes;
    }

    public IReadOnlyList<uint> GetHairColorIds(uint modelId) => _palettes.TryGetValue(modelId, out var p) ? p.Hair : [];
    public IReadOnlyList<uint> GetSkinColorIds(uint modelId) => _palettes.TryGetValue(modelId, out var p) ? p.Skin : [];
    public IReadOnlyList<uint> GetNormalMapIds(uint modelId) => _palettes.TryGetValue(modelId, out var p) ? p.NormalMaps : [];
    public IReadOnlyList<byte[]> GetFacePresetModifiers(uint modelId) => _palettes.TryGetValue(modelId, out var p) ? p.Presets : [];
    public IReadOnlyList<BotMovableDecal> GetMovableDecals(uint modelId) => _palettes.TryGetValue(modelId, out var p) ? p.Movable : [];

    public IReadOnlyList<uint> GetFixedDecalIds(uint modelId, byte categoryId)
        => _palettes.TryGetValue(modelId, out var p) && p.FixedDecals.TryGetValue(categoryId, out var ids) ? ids : [];
}

/// <summary>
/// Static starting-equipment source for rigs. The production source is
/// CharacterManager (the same _abilityItems + template data the human create
/// path reads).
/// </summary>
public sealed class StaticBotStartingEquipmentSource : IBotStartingEquipmentSource
{
    private readonly Dictionary<byte, AbilityItems> _byAbility;
    private readonly Dictionary<(Race, Gender), uint[]> _bodyItems;

    public StaticBotStartingEquipmentSource(
        Dictionary<byte, AbilityItems> byAbility,
        Dictionary<(Race, Gender), uint[]> bodyItems)
    {
        _byAbility = byAbility;
        _bodyItems = bodyItems;
    }

    public AbilityItems GetAbilityEquipment(byte abilityId)
        => _byAbility.TryGetValue(abilityId, out var items) ? items
            : new AbilityItems { Ability = abilityId, Items = new EquipItemsTemplate() };

    public uint[] GetBodyItems(Race race, Gender gender)
        => _bodyItems.TryGetValue((race, gender), out var items) ? items : new uint[7];
}
