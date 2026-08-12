using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Units;
using NLog;

namespace AAEmu.Game.Models.Game.Bots;

/// <summary>
/// Player-like appearance factory for bot citizens (P1 t_61814965 — the
/// durable path behind the 1,000-citizen vision; Josh's model: bots are
/// players, so their look is generated the way a player's character is).
///
/// Generation mirrors the human create path (CSCreateCharacterPacket →
/// CharacterManager.Create) for every dimension:
///
///  * race/gender → canonical model id (characters.model_id: 10/11 Nuian,
///    16/17 Elf, 18/19 Dwarf, 20/21 Hariharan) — the base mesh the 1.2
///    client loads. Wrong model id = invisible body (the modelId=733
///    lesson).
///  * unit_model_params: type=Face + hair/skin color ids + face features,
///    ALL drawn from the canonical 1.2 catalogs (hair_colors, skin_colors,
///    face_decal_assets, face_normal_maps, custom_face_presets — npc_only
///    rows excluded). Any catalog that is empty falls back to the
///    render-proven canonical default (BotAppearanceDefaults).
///  * starting equipment: per-class newbie gear pack + race body items +
///    newbie consumables — the exact data the human path reads.
///  * name: race/gender pool, NameManager-valid.
///
/// Determinism: a <see cref="BotAppearanceSpec"/> with a seed (or a name,
/// which hashes to a seed) produces the byte-identical appearance — the
/// template-system hook for reproducible looks and tests.
/// </summary>
public sealed class BotAppearanceFactory
{
    /// <summary>Abilities whose 1.2 data defines a newbie gear pack (character_equip_packs).</summary>
    private static readonly AbilityType[] PackBearingAbilities =
    [
        AbilityType.Fight, AbilityType.Death, AbilityType.Wild,
        AbilityType.Magic, AbilityType.Vocation, AbilityType.Love
    ];

    /// <summary>Curated tint palettes — player-like, never neon (alpha preserved).</summary>
    private static readonly uint[] PupilTints =
        [0xFFF8B55Au, 0xFFE8C07Au, 0xFFC8A66Au, 0xFF9C8A6Au, 0xFF6E7F8Au, 0xFF7A5C3Au, 0xFF4A6E8Au];

    private static readonly uint[] EyebrowTints =
        [0xFF00233Cu, 0xFF1A1208u, 0xFF2B1D0Fu, 0xFF3A2A16u, 0xFF4A3520u, 0xFF5C462Cu, 0xFF080808u];

    private static readonly uint[] DecoTints =
        [0xFF483E60u, 0xFF5A4A3Au, 0xFF3A4A5Au, 0xFF6A5A4Au, 0xFF4A3A2Au, 0xFF2A3A4Au, 0xFF8A6A4Au];

    private readonly IBotAppearanceColorSource _colors;
    private readonly IBotStartingEquipmentSource _equipment;

    /// <summary>Production instance: canonical 1.2 catalogs + the game's own starting-equipment data.</summary>
    public static BotAppearanceFactory Instance { get; } = new(
        new SqliteBotAppearanceColorSource(),
        new CharacterManagerEquipmentSource());

    public BotAppearanceFactory(IBotAppearanceColorSource colors, IBotStartingEquipmentSource equipment)
    {
        _colors = colors ?? throw new ArgumentNullException(nameof(colors));
        _equipment = equipment ?? throw new ArgumentNullException(nameof(equipment));
    }

    /// <summary>
    /// Generates a full player-like appearance from the spec. Deterministic
    /// for a given seed — the same seed always yields the same name, model,
    /// blob and equipment plan.
    /// </summary>
    public BotAppearance Generate(BotAppearanceSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);

        var modelId = BotAppearanceDefaults.ModelIdFor(spec.Race, spec.Gender);
        var random = new Random(unchecked((int)ResolveSeed(spec)));

        var name = spec.Name ?? BotNamePool.Pick(spec.Race, spec.Gender, random);
        var classAbility = spec.ClassAbility ?? PackBearingAbilities[random.Next(PackBearingAbilities.Length)];

        var abilityItems = _equipment.GetAbilityEquipment((byte)classAbility);
        var equipment = BotEquipmentPlan.FromAbilityItems(abilityItems.Items);
        var bodyItems = _equipment.GetBodyItems(spec.Race, spec.Gender) ?? [];

        return new BotAppearance
        {
            Name = name,
            Race = spec.Race,
            Gender = spec.Gender,
            ModelId = modelId,
            ModelParams = BuildModelParams(modelId, random),
            ClassAbility = classAbility,
            Equipment = equipment,
            BodyItems = bodyItems,
            Supplies = abilityItems.Supplies.Select(s => new BotSupplyEntry(s.Id, s.Amount, s.Grade)).ToList()
        };
    }

    /// <summary>Seed resolution: explicit seed wins, then stable hash of the name, then non-reproducible.</summary>
    private static uint ResolveSeed(BotAppearanceSpec spec)
    {
        if (spec.Seed is { } seed)
            return seed;

        if (spec.Name is { Length: > 0 } name)
            return Fnv1a(name);

        return (uint)Random.Shared.Next();
    }

    /// <summary>FNV-1a 32-bit — the stable per-name seed (same name → same look across reboots).</summary>
    public static uint Fnv1a(string text)
    {
        var hash = 2166136261u;
        foreach (var c in text)
        {
            hash ^= c;
            hash *= 16777619u;
        }
        return hash;
    }

    private UnitCustomModelParams BuildModelParams(uint modelId, Random random)
    {
        var face = new FaceModel
        {
            MovableDecalWeight = 1.0f,
            MovableDecalScale = 1.0f,
            MovableDecalRotate = 0f,
            DiffuseMapId = 0,
            EyelashMapId = 0,
            NormalMapWeight = 1.0f,
            LipColor = 0,
            LeftPupilColor = Pick(PupilTints, random),
            RightPupilColor = Pick(PupilTints, random),
            EyebrowColor = Pick(EyebrowTints, random),
            DecoColor = Pick(DecoTints, random)
        };

        // Face morph: a real character-creation preset's modifier block
        // (custom_face_presets — 36 per model). Falls back to the default
        // face (zeroed modifier) which is render-proven.
        var presets = _colors.GetFacePresetModifiers(modelId);
        if (presets.Count > 0)
            face.Modifier = (byte[])presets[random.Next(presets.Count)].Clone();

        // Fixed decals: one per category slot, like the human screen offers
        // (tattoo/scar, makeup, eyebrow, deco). Some slots are left empty for
        // variety; missing categories on a model are skipped.
        SetFixedDecal(face, 0, 2, modelId, random, probability: 0.55);
        SetFixedDecal(face, 1, 3, modelId, random, probability: 0.55);
        SetFixedDecal(face, 2, 4, modelId, random, probability: 0.85);
        SetFixedDecal(face, 3, 5, modelId, random, probability: 0.85);

        // Movable decal (makeup mark etc.) at its canonical default placement.
        var movable = _colors.GetMovableDecals(modelId);
        if (movable.Count > 0 && random.NextDouble() < 0.6)
        {
            var decal = movable[random.Next(movable.Count)];
            face.MovableDecalAssetId = decal.AssetId;
            face.MovableDecalMoveX = (short)(decal.DefaultX + random.Next(-8, 9));
            face.MovableDecalMoveY = (short)(decal.DefaultY + random.Next(-8, 9));
        }

        // Normal map (facial structure) — the prod blob used model 10's id 29.
        var normalMaps = _colors.GetNormalMapIds(modelId);
        face.NormalMapId = normalMaps.Count > 0 ? normalMaps[random.Next(normalMaps.Count)] : 0u;

        var hair = _colors.GetHairColorIds(modelId);
        var skin = _colors.GetSkinColorIds(modelId);
        var canonical = BotAppearanceDefaults.CanonicalColorsFor(modelId);

        return new UnitCustomModelParams(UnitCustomModelType.Face)
            .SetHairColorId(hair.Count > 0 ? hair[random.Next(hair.Count)] : canonical.Hair)
            .SetSkinColorId(skin.Count > 0 ? skin[random.Next(skin.Count)] : canonical.Skin)
            .SetModelId(0) // base model comes from Character.ModelId (template)
            .SetFace(face);
    }

    private void SetFixedDecal(FaceModel face, byte slot, byte category, uint modelId, Random random, double probability)
    {
        if (random.NextDouble() >= probability)
            return;

        var decals = _colors.GetFixedDecalIds(modelId, category);
        if (decals.Count == 0)
            return;

        face.SetFixedDecalAsset(slot, decals[random.Next(decals.Count)], 1.0f);
    }

    private static uint Pick(uint[] palette, Random random)
        => palette[random.Next(palette.Length)];
}

/// <summary>
/// Production starting-equipment source: the game's OWN CharacterManager
/// data — the identical _abilityItems (character_equip_packs +
/// character_supplies) and character-template body items (item_body_parts)
/// the human create path reads. Any gap (template/manager not loaded yet)
/// degrades to an empty plan — no appearance ever fails on equipment.
/// </summary>
public sealed class CharacterManagerEquipmentSource : IBotStartingEquipmentSource
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    public AbilityItems GetAbilityEquipment(byte abilityId)
    {
        try
        {
            var manager = AAEmu.Game.Core.Managers.UnitManagers.CharacterManager.Instance;
            return manager.GetStartingAbilityEquipment(abilityId);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Bot appearance: starting-equipment lookup failed for ability {AbilityId}; bot ships gearless (empty equipment plan)", abilityId);
            return new AbilityItems { Ability = abilityId, Items = new EquipItemsTemplate() };
        }
    }

    public uint[] GetBodyItems(Race race, Gender gender)
    {
        try
        {
            var manager = AAEmu.Game.Core.Managers.UnitManagers.CharacterManager.Instance;
            return manager.GetTemplate(race, gender).Items;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Bot appearance: body-item lookup failed for race {Race} gender {Gender}; bot ships without body items", race, gender);
            return new uint[7];
        }
    }
}
