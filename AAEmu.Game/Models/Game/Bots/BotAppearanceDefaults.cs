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
/// DEMO BODY SOURCE (Josh direction 2026-08-08): bots are players → they
/// need player-like appearance. FOR NOW the demo source is Asssaa's exact
/// appearance (unit_model_params 231B with hair/model 733 + her equipped
/// items) so bodies render immediately; the full per-race/gender factory is
/// a separate card. The blob is embedded VERBATIM (prod MySQL HEX, verified
/// 2026-08-08) and round-tripped through Read/Write so the wire bytes are
/// byte-identical to the rendering human. Other models fall back to the
/// canonical per-model builder from hotfix #4.
/// </summary>
public static class BotAppearanceDefaults
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>
    /// Asssaa (id 6, race 1 / gender 1, model 10) unit_model_params blob —
    /// the DEMO body source, byte-for-byte from prod MySQL HEX
    /// (LENGTH=231). Head: 03 DD020000 01000000 00000000 ... (type=Face,
    /// hair 733, skin 1, model 0 + full FaceModel with modifier).
    /// </summary>
    private const string DemoBlobHex =
        "03DD0200000100000000000000000000000000803F0000803F0000000000000000000000000000803F000000000000803F300200000000803FAA" +
        "0200000000803F000000001D000000000000000000803F000000005AB5F8FF5AB5F8FF3C2300FF603E48FF800000F5000011DC000B0000000017" +
        "0000000000F323000000003D00000000000000000000000000000000000000000000000100000000000000000000000000000000000000000000" +
        "000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000";

    /// <summary>
    /// Asssaa's equipped items (prod MySQL, 2026-08-08): slot → template id.
    /// The demo bots replicate this exact set so the client receives the
    /// same equipment section as the rendering human.
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

    /// <summary>
    /// Canonical first hair/skin color id per character model id
    /// (compact.sqlite3: characters.model_id → hair_colors/skin_colors).
    /// Verified live 2026-08-08. Fallback path for non-demo models.
    /// </summary>
    private static readonly IReadOnlyDictionary<uint, (uint Hair, uint Skin)> CanonicalColors =
        new Dictionary<uint, (uint Hair, uint Skin)>
        {
            [10] = (733, 1),  // Nuian male — demo (Asssaa's hair/skin)
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
    /// Builds the default custom model params for a bot.
    ///
    /// DEMO (model 10): returns the EXACT Asssaa blob (embedded verbatim),
    /// so the wire bytes are byte-identical to the rendering human — head
    /// <c>03 DD020000 01000000</c> (hair/model 733). Other models: the
    /// canonical type=Face builder (231-byte structure, per-model hair/skin).
    /// </summary>
    public static UnitCustomModelParams BuildDefault(Race race, Gender gender, uint modelId)
    {
        if (modelId == 10)
            return ReadDemoBlob();

        var colors = CanonicalColors.TryGetValue(modelId, out var c) ? c : (Hair: 1u, Skin: 1u);

        var face = new FaceModel
        {
            MovableDecalWeight = 1.0f,
            MovableDecalScale = 1.0f,
            DiffuseMapId = 0,
            NormalMapId = 0,
            EyelashMapId = 0,
            NormalMapWeight = 1.0f,
            LipColor = 0,
            LeftPupilColor = 0,
            RightPupilColor = 0,
            EyebrowColor = 0,
            DecoColor = 0
        };

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
    /// True when the params serialize to the embedded demo blob (Asssaa's
    /// exact 231 bytes). Used by the adopt path to detect rows still carrying
    /// the pre-demo blob (231B but hair 1 instead of 733) and upgrade them to
    /// the demo appearance so the wire bytes match the rendering human.
    /// </summary>
    public static bool IsDemoAppearance(UnitCustomModelParams modelParams)
    {
        if (modelParams == null)
            return false;

        var bytes = modelParams.Write(new PacketStream()).GetBytes();
        return bytes.SequenceEqual(Convert.FromHexString(DemoBlobHex));
    }

    /// <summary>
    /// Equips the bot's body items so the 1.2 client can assemble the
    /// character mesh from the SCUnitStatePacket equipment section.
    ///
    /// DEMO (model 10): replicates Asssaa's exact equipment (her 10 items
    /// at their slots). Other models: template body parts (face/hair/body,
    /// slots 19-25 from CharacterTemplate.Items), mirroring the human
    /// create path (CharacterManager.Create: bodyItems[i] → (i + 19)).
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

    /// <summary>
    /// Parses the embedded demo blob back into a UnitCustomModelParams.
    /// Read/Write are symmetric (ReadBytes uses an Int16 length prefix), so
    /// Write() reproduces the embedded bytes exactly.
    /// </summary>
    private static UnitCustomModelParams ReadDemoBlob()
    {
        var bytes = Convert.FromHexString(DemoBlobHex);
        var stream = new PacketStream(bytes);
        var modelParams = new UnitCustomModelParams();
        modelParams.Read(stream);
        return modelParams;
    }
}
