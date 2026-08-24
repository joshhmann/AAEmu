namespace AAEmu.Game.Models.Game.Mate;

/// <summary>
/// Template of the mate_equip_slot_packs table.
/// Defines which equipment slots a category of mate (riding summon, battle summon, pet) accepts.
/// </summary>
public class MateEquipSlotPack
{
    public uint Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool Head { get; set; }
    public bool Chest { get; set; }
    public bool Waist { get; set; }
    public bool Feet { get; set; }
}
