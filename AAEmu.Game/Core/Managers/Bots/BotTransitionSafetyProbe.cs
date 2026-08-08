using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.Skills;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// Production default for <see cref="IBotTransitionSafetyProbe"/> — reads live
/// Character state, null-safe so un-embodied or test characters never throw.
///
/// Mapping (spec §11 verbatim list, code-validated in ARCHITECTURE_REVIEW):
/// - combat            → Character.IsInBattle (Unit.cs:240)
/// - attached to Slave → ParentWorld.SlaveManager.GetActiveSlaveByOwnerObjId
///                       (CSSelectCharacterPacket.cs:49 pattern)
/// - trade pack        → Backpack equipment slot holds a BackpackTemplate item
///                       (Inventory.TryEquipNewBackPack / IsAutoEquipTradePack)
/// - in trial          → Buffs.CheckBuff(ForciblyAwaitingTrial) (Character.cs:3053)
/// - grouped w/ human  → Character.InParty (Character.cs:276)
/// - saving            → no save-in-progress signal exists on Character today;
///                       the H4 SaveManager dirty-flush slice is the wiring point.
/// </summary>
public sealed class BotTransitionSafetyProbe : IBotTransitionSafetyProbe
{
    /// <inheritdoc />
    public bool IsInCombat(Character character) => character.IsInBattle;

    /// <inheritdoc />
    public bool IsAttachedToSlave(Character character)
        => character.ParentWorld?.SlaveManager.GetActiveSlaveByOwnerObjId(character.ObjId) != null;

    /// <inheritdoc />
    public bool IsCarryingTradePack(Character character)
    {
        var backpackItem = character.Inventory?.Equipment?.GetItemBySlot((int)EquipmentItemSlot.Backpack);
        return backpackItem?.Template is BackpackTemplate;
    }

    /// <inheritdoc />
    public bool IsInTrial(Character character)
        => character.Buffs?.CheckBuff((uint)BuffConstants.ForciblyAwaitingTrial) ?? false;

    /// <inheritdoc />
    public bool IsGroupedWithHuman(Character character) => character.InParty;

    /// <inheritdoc />
    public bool IsSaving(Character character) => false; // no save-in-progress signal yet (H4 wiring point)
}
