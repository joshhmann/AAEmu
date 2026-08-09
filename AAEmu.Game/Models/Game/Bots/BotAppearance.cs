using AAEmu.Commons.Network;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.Game.Models.Game.Bots;

/// <summary>
/// A full player-like appearance for a bot (P1 t_61814965 — durable factory
/// behind the 1,000-citizen vision). Mirrors the HUMAN create-path shape
/// (CSCreateCharacterPacket → CharacterManager.Create): randomized but
/// valid unit_model_params (type=Face), race/gender-canonical model id,
/// per-class starting equipment plan, and a character name from the pool.
///
/// What renders: <see cref="ModelParams"/> is the exact blob the 1.2 client
/// uses to build the character mesh (the modelId=733 lesson: wrong model id
/// or a degenerate blob = invisible body — see BotAppearanceDefaults for the
/// P0 evidence). Equipment is applied through the ordinary provisioning path
/// (AGENTS.md #9/#10 — bots stay ordinary Characters).
/// </summary>
public sealed class BotAppearance
{
    /// <summary>Character display name (NameManager-valid).</summary>
    public required string Name { get; init; }

    /// <summary>Race.</summary>
    public required Race Race { get; init; }

    /// <summary>Gender.</summary>
    public required Gender Gender { get; init; }

    /// <summary>
    /// Canonical character model id for the race/gender (characters.model_id:
    /// 10/11 Nuian, 16/17 Elf, 18/19 Dwarf, 20/21 Hariharan). The row's
    /// ModelId drives the base mesh the client loads.
    /// </summary>
    public required uint ModelId { get; init; }

    /// <summary>
    /// Randomized-but-valid custom model params (type=Face). Serializes to
    /// the same 231-byte structure as a real human create blob.
    /// </summary>
    public required UnitCustomModelParams ModelParams { get; init; }

    /// <summary>
    /// Primary class (ability 1). The starting gear pack is resolved per
    /// class exactly like the human create path resolves
    /// <c>_abilityItems[ability1]</c>.
    /// </summary>
    public required AbilityType ClassAbility { get; init; }

    /// <summary>
    /// Per-class starting equipment plan (mirror of the human path's
    /// equip-pack application). Empty for classes whose 1.2 data defines no
    /// newbie pack — a human of that class starts naked too (canonical).
    /// </summary>
    public required IReadOnlyList<BotEquipEntry> Equipment { get; init; }

    /// <summary>
    /// The 7 body-item template ids for the race/gender (item_body_parts →
    /// CharacterTemplate.Items, equipment slots Face..Beard 19..25 — the same
    /// fallback the human create path applies when the client sends zeros).
    /// </summary>
    public required IReadOnlyList<uint> BodyItems { get; init; }

    /// <summary>Per-class newbie consumables (bag + action slots), like the human path.</summary>
    public required IReadOnlyList<BotSupplyEntry> Supplies { get; init; }

    /// <summary>The appearance blob exactly as it is persisted (unit_model_params row).</summary>
    public byte[] Blob => ModelParams.Write(new PacketStream()).GetBytes();

    /// <summary>True when the plan carries at least one equippable item.</summary>
    public bool HasEquipment => Equipment.Count > 0 || Supplies.Count > 0;
}

/// <summary>One equipped item in a starting-equipment plan.</summary>
public sealed record BotEquipEntry(uint TemplateId, EquipmentItemSlot Slot, byte Grade);

/// <summary>One newbie consumable in a starting-equipment plan.</summary>
public sealed record BotSupplyEntry(uint TemplateId, int Amount, byte Grade);

/// <summary>
/// Describes what a bot SHOULD look like. Everything is optional — the
/// factory fills the gaps with randomized-but-valid values.
/// </summary>
/// <param name="Race">Race. Required.</param>
/// <param name="Gender">Gender. Required.</param>
/// <param name="ClassAbility">
/// Primary class. Null → the factory picks one from the abilities whose 1.2
/// data defines a newbie gear pack (Fight/Death/Wild/Magic/Vocation/Love) so
/// every generated citizen starts with real equipment.
/// </param>
/// <param name="Seed">
/// Deterministic seed — the same seed + same data produces the byte-identical
/// appearance (tests, template-specified looks). Null → derived from
/// <paramref name="Name"/> when present (stable per name), else non-
/// reproducible.
/// </param>
/// <param name="Name">
/// Character name. Null → the factory draws one from the race/gender pool.
/// Must be NameManager-valid when provided (^[a-zA-Z0-9а-яА-Я]{1,18}$).
/// </param>
public sealed record BotAppearanceSpec(
    Race Race,
    Gender Gender,
    AbilityType? ClassAbility = null,
    uint? Seed = null,
    string? Name = null);

/// <summary>
/// Converts the 1.2 starting-gear data (AbilityItems + CharacterTemplate
/// body items — the same data the human create path reads) into a
/// <see cref="BotAppearance"/> equipment plan. Slot mapping mirrors
/// CharacterManager.Create exactly (14 equip-pack slots + 7 body slots
/// Face..Beard 19..25 + newbie supplies).
/// </summary>
public static class BotEquipmentPlan
{
    public static IReadOnlyList<BotEquipEntry> FromAbilityItems(EquipItemsTemplate items)
    {
        var entries = new List<BotEquipEntry>(16);
        Add(entries, items.Headgear, EquipmentItemSlot.Head, items.HeadgearGrade);
        Add(entries, items.Necklace, EquipmentItemSlot.Neck, items.NecklaceGrade);
        Add(entries, items.Shirt, EquipmentItemSlot.Chest, items.ShirtGrade);
        Add(entries, items.Belt, EquipmentItemSlot.Waist, items.BeltGrade);
        Add(entries, items.Pants, EquipmentItemSlot.Legs, items.PantsGrade);
        Add(entries, items.Gloves, EquipmentItemSlot.Hands, items.GlovesGrade);
        Add(entries, items.Shoes, EquipmentItemSlot.Feet, items.ShoesGrade);
        Add(entries, items.Bracelet, EquipmentItemSlot.Arms, items.BraceletGrade);
        Add(entries, items.Back, EquipmentItemSlot.Back, items.BackGrade);
        Add(entries, items.Undershirts, EquipmentItemSlot.Undershirt, items.UndershirtsGrade);
        Add(entries, items.Underpants, EquipmentItemSlot.Underpants, items.UnderpantsGrade);
        Add(entries, items.Mainhand, EquipmentItemSlot.Mainhand, items.MainhandGrade);
        Add(entries, items.Offhand, EquipmentItemSlot.Offhand, items.OffhandGrade);
        Add(entries, items.Ranged, EquipmentItemSlot.Ranged, items.RangedGrade);
        Add(entries, items.Musical, EquipmentItemSlot.Musical, items.MusicalGrade);
        Add(entries, items.Cosplay, EquipmentItemSlot.Cosplay, items.CosplayGrade);
        return entries;
    }

    private static void Add(List<BotEquipEntry> entries, uint templateId, EquipmentItemSlot slot, byte grade)
    {
        if (templateId > 0)
            entries.Add(new BotEquipEntry(templateId, slot, grade));
    }
}
