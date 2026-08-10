using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.Game.Models.Game.Bots;

/// <summary>
/// Race-appropriate default character appearance for headless-provisioned
/// bots (P0 hotfix t_76730833).
///
/// PROD EVIDENCE (2026-08-08): bots provisioned with a bare
/// <c>new UnitCustomModelParams()</c> serialize a 1-byte blob (type=None) —
/// the prod rows for Citizen01-03 carry exactly <c>00</c> — and the 1.2
/// client cannot build the character mesh from empty custom model params:
/// name tags + positions render, the body does not (Josh's sighting at
/// 15572,15364,126.5, zone 179). A real human row (Asssaa, id 6) carries a
/// 231-byte blob: type=Face, hair 733, skin 1, model 0 + full FaceModel.
///
/// This factory reproduces the human create-path shape (what
/// CSCreateCharacterPacket → CharacterManager.Create stores for a real
/// player) with canonical per-model defaults from compact.sqlite3:
///   hair_colors / skin_colors MIN(id) per characters.model_id.
/// The Nuian-male face mirrors the prod human blob (fixed decals 560/682,
/// normal map 29, pupil/eyebrow/deco colors) — the exact shape proven to
/// render on Josh's client; other races get a default (decal-free) face
/// with the same structure.
/// </summary>
public static class BotAppearanceDefaults
{
    /// <summary>
    /// Canonical first hair/skin color id per character model id
    /// (compact.sqlite3: characters.model_id → hair_colors/skin_colors).
    /// Verified live 2026-08-08.
    /// </summary>
    private static readonly IReadOnlyDictionary<uint, (uint Hair, uint Skin)> CanonicalColors =
        new Dictionary<uint, (uint Hair, uint Skin)>
        {
            [10] = (1, 1),    // Nuian male
            [11] = (36, 3),   // Nuian female
            [16] = (84, 5),   // Elf male
            [17] = (97, 7),   // Elf female
            [18] = (117, 13), // Dwarf male
            [19] = (135, 15), // Dwarf female
            [20] = (155, 9),  // Hariharan male
            [21] = (167, 11), // Hariharan female
        };

    /// <summary>
    /// Canonical character model id per race/gender (characters.model_id:
    /// 10/11 Nuian, 16/17 Elf, 18/19 Dwarf, 20/21 Hariharan) — the base mesh
    /// the 1.2 client loads for a player of that race/gender. The
    /// modelId=733 lesson: any other id = invisible body.
    /// </summary>
    public static uint ModelIdFor(Race race, Gender gender) => (race, gender) switch
    {
        (Race.Nuian, Gender.Male) => 10,
        (Race.Nuian, Gender.Female) => 11,
        (Race.Elf, Gender.Male) => 16,
        (Race.Elf, Gender.Female) => 17,
        (Race.Dwarf, Gender.Male) => 18,
        (Race.Dwarf, Gender.Female) => 19,
        (Race.Hariharan, Gender.Male) => 20,
        (Race.Hariharan, Gender.Female) => 21,
        _ => 10
    };

    /// <summary>Canonical first hair/skin color id per character model id (see <see cref="CanonicalColors"/>).</summary>
    public static (uint Hair, uint Skin) CanonicalColorsFor(uint modelId)
        => CanonicalColors.TryGetValue(modelId, out var colors) ? colors : (1u, 1u);

    /// <summary>
    /// Nuian-male face values copied from the prod human blob (Asssaa id 6)
    /// — the shape that demonstrably renders. Used verbatim for model 10;
    /// other models get a structurally identical face with zero decals.
    /// </summary>
    public static UnitCustomModelParams BuildDefault(Race race, Gender gender, uint modelId)
    {
        var colors = CanonicalColors.TryGetValue(modelId, out var c) ? c : (Hair: 1u, Skin: 1u);
        var isNuianMale = modelId == 10;

        var face = new FaceModel
        {
            MovableDecalWeight = 1.0f,
            MovableDecalScale = 1.0f,
            DiffuseMapId = 0,
            NormalMapId = isNuianMale ? 29u : 0u,
            EyelashMapId = 0,
            NormalMapWeight = 1.0f,
            LipColor = 0,
            LeftPupilColor = isNuianMale ? 0xFFF8B55Au : 0u,
            RightPupilColor = isNuianMale ? 0xFFF8B55Au : 0u,
            EyebrowColor = isNuianMale ? 0xFF00233Cu : 0u,
            DecoColor = isNuianMale ? 0xFF483E60u : 0u
        };

        if (isNuianMale)
        {
            // nu_m_decal_eyebrow001 / nu_m_decal_deco002 (model 10)
            face.SetFixedDecalAsset(2, 560u, 1.0f);
            face.SetFixedDecalAsset(3, 682u, 1.0f);
        }

        return new UnitCustomModelParams(UnitCustomModelType.Face)
            .SetHairColorId(colors.Hair)
            .SetSkinColorId(colors.Skin)
            .SetModelId(0) // base model comes from Character.ModelId (template)
            .SetFace(face);
    }

    /// <summary>
    /// True when the params carry no customization at all (type=None — the
    /// pre-hotfix bot blob). Used by the adopt path to heal legacy rows.
    /// </summary>
    public static bool IsDegenerate(UnitCustomModelParams modelParams)
    {
        if (modelParams == null)
            return true;

        var bytes = modelParams.Write(new AAEmu.Commons.Network.PacketStream()).GetBytes();
        return bytes.Length <= 1 && bytes[0] == (byte)UnitCustomModelType.None;
    }
}
