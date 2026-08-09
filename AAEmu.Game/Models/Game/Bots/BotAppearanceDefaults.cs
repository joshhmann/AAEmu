using AAEmu.Commons.Network;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Char.Templates;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Game.Units;

using NLog;

namespace AAEmu.Game.Models.Game.Bots;

/// <summary>
/// Default character appearance for headless-provisioned bots.
///
/// P0 hotfix t_76730833 (blob): bots provisioned with a bare
/// <c>new UnitCustomModelParams()</c> serialize a 1-byte blob (type=None) —
/// the prod rows for Citizen01-03 carried exactly <c>00</c> — and the 1.2
/// client cannot build the character mesh from empty custom model params:
/// name tags + positions render, the body does not.
///
/// P0 hotfix t_d0889187 (FINAL layer): after the blob heal, bot rows
/// Citizen01-03 STILL had ZERO items in every container while a real human
/// (Asssaa, id 6) carries a full 231-byte blob + body-part/gear items.
/// The 1.2 client builds the character mesh from the EQUIPMENT section of
/// SCUnitStatePacket — Inventory_Equip writes item.TemplateId for body-part
/// slots 19-26 and the NPC path forces SetType(Skin) when validFlags &lt;= 0
/// ("no body and no face", SCUnitStatePacket.cs:406). A unit with an empty
/// equipment container renders name tag + position but no body.
///
/// t_555ed207 (class fix): the t_d0889187 "demo body source" force-stamped
/// Asssaa's EXACT blob for model 10 — BuildDefault(modelId==10) returned the
/// embedded demo blob and the adopt path replaced ANY non-demo blob with it
/// on every boot, collapsing all factory-distinct looks to one identical
/// appearance after any reboot. That path is REMOVED. BuildDefault now
/// returns the canonical per-model builder for EVERY model (the render-
/// proven hotfix-#4 shape: type=Face + canonical hair/skin + Nuian-male
/// face), so no code path can collapse distinct looks via a "default".
/// Appearance distinctness is the BotAppearanceFactory's job (t_61814965);
/// defaults are only for rows with no look at all.
/// </summary>
public static class BotAppearanceDefaults
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

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
    /// Builds the canonical default custom model params for a bot.
    ///
    /// ALL models (including 10) go through the per-model canonical builder:
    /// type=Face, canonical first hair/skin ids from compact.sqlite3, and the
    /// Nuian-male face shape (fixed decals 560/682, normal map 29) proven to
    /// render on Josh's client (hotfix #4). Serializes to the same 231-byte
    /// structure as a human create-path row.
    ///
    /// t_555ed207: model 10 NO LONGER returns Asssaa's embedded demo blob —
    /// that special case collapsed every Nuian-male bot to one identical
    /// look whenever any code path called BuildDefault. A default must be a
    /// valid look, never a distinctness destroyer.
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

        var bytes = modelParams.Write(new PacketStream()).GetBytes();
        return bytes.Length <= 1 && bytes[0] == (byte)UnitCustomModelType.None;
    }

    /// <summary>
    /// Equips the bot's body items so the 1.2 client can assemble the
    /// character mesh from the SCUnitStatePacket equipment section.
    ///
    /// Model 10 replicates Asssaa's demo equipment (her 10 items at their
    /// slots — the demo body source from t_d0889187, kept as the canonical
    /// model-10 gear set). Other models: template body parts (face/hair/body,
    /// slots 19-25 from CharacterTemplate.Items), mirroring the human
    /// create path (CharacterManager.Create: bodyItems[i] → (i + 19)).
    ///
    /// Idempotent: only fires when a row has NO body-part items, so it never
    /// overwrites factory-born equipment and never touches the appearance
    /// blob (t_555ed207 scope: blob distinctness is preserved).
    ///
    /// Returns the number of items actually equipped.
    /// </summary>
    public static int EquipTemplateBodyParts(Character character, CharacterTemplate template)
    {
        if (character?.Inventory?.Equipment == null || template == null)
            return 0;

        if (template.ModelId == 10)
            return EquipDemoItems(character);

        return EquipTemplateBodyPartItems(character, template);
    }

    /// <summary>
    /// Asssaa's equipped items (prod MySQL, 2026-08-08): slot → template id.
    /// The model-10 gear set mirrors the rendering human's exact equipment.
    /// </summary>
    private static readonly IReadOnlyDictionary<int, uint> DemoEquipment =
        new Dictionary<int, uint>
        {
            [2] = 23387,   // Chest
            [4] = 23388,   // Legs
            [6] = 23390,   // Feet
            [15] = 5569,   // Mainhand
            [16] = 6152,   // Offhand
            [17] = 6127,   // Ranged
            [18] = 6177,   // Musical
            [19] = 19838,  // Face (nu_m_face00)
            [20] = 24133,  // Hair
            [24] = 536,    // Body (nu_m_nude)
        };

    private static int EquipDemoItems(Character character)
    {
        var equipped = 0;
        foreach (var (slot, templateId) in DemoEquipment)
        {
            var item = ItemManager.Instance.Create(templateId, 1, 0);
            if (item == null)
            {
                Logger.Warn($"BotAppearanceDefaults: demo item template {templateId} (slot {slot}) could not be created — skipping");
                continue;
            }

            item.SlotType = SlotType.Equipment;
            item.Slot = slot;
            if (character.Inventory.Equipment.AddOrMoveExistingItem(ItemTaskType.Invalid, item, slot))
                equipped++;
            else
                Logger.Warn($"BotAppearanceDefaults: could not equip demo item {templateId} into slot {slot}");
        }

        return equipped;
    }

    private static int EquipTemplateBodyPartItems(Character character, CharacterTemplate template)
    {
        var equipped = 0;
        for (var i = 0; i < 7; i++)
        {
            var templateId = i < template.Items.Length ? template.Items[i] : 0u;
            if (templateId <= 0)
                continue;

            // Mirrors CharacterManager.SetEquipItemTemplate + the bodyItems
            // loop in Create(): slot i+19 = Face(19)…Beard(25).
            var item = ItemManager.Instance.Create(templateId, 1, 0);
            if (item == null)
            {
                Logger.Warn($"BotAppearanceDefaults: item template {templateId} (body part slot {i + 19}) could not be created — skipping");
                continue;
            }

            item.SlotType = SlotType.Equipment;
            item.Slot = (int)(EquipmentItemSlot)(i + 19);
            if (character.Inventory.Equipment.AddOrMoveExistingItem(ItemTaskType.Invalid, item, (int)(EquipmentItemSlot)(i + 19)))
                equipped++;
            else
                Logger.Warn($"BotAppearanceDefaults: could not equip body part {templateId} into slot {i + 19}");
        }

        return equipped;
    }
}
