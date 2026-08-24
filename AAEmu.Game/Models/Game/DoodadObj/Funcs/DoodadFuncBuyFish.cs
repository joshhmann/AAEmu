using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.Game.Models.Game.DoodadObj.Funcs;

public class DoodadFuncBuyFish : DoodadFuncTemplate
{
    // doodad_funcs
    public uint ItemId { get; set; }

    public override void Use(BaseUnit caster, Doodad owner, uint skillId, int nextPhase = 0)
    {
        Logger.Trace("DoodadFuncBuyFish");

        if (caster is Character character)
        {
            var backpack = character.Inventory.GetEquippedBySlot(EquipmentItemSlot.Backpack);
            if (backpack == null)
            {
                character.SendErrorMessage(ErrorMessageType.StoreBackpackNogoods);
                return;
            }

            owner.ItemTemplateId = backpack.TemplateId; // to display the phase animation correctly for doodad

            // Sell the fish bundle: single currency credit through the normal
            // money path (the previous code credited `Money += total` AND
            // AddMoney — double-paying the seller).
            var total = backpack.Template.Refund;

            character.Equipment.RemoveItem(ItemTaskType.SkillEffectConsumption, backpack, true);
            character.AddMoney(SlotType.Inventory, total);
        }
    }
}
