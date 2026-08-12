using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.Game.Models.Game.DoodadObj.Funcs;

public class DoodadFuncFeed : DoodadFuncTemplate
{
    // doodad_funcs
    public uint ItemId { get; set; }
    public int Count { get; set; }

    public override void Use(BaseUnit caster, Doodad owner, uint skillId, int nextPhase = 0)
    {
        if (caster is not Character character)
            return;

        // 1.2 feed func (doodad_func_feeds): feeding consumes the configured
        // feed item from the caster's inventory (e.g. feed 14310, mackerel 797
        // ×1). When the player is short on feed the interaction is refused
        // with the client "not_enough_item" error and nothing is consumed.
        var required = Math.Max(Count, 1);
        if (character.Inventory.GetItemsCount(ItemId) < required)
        {
            character.SendErrorMessage(ErrorMessageType.NotEnoughItem);
            return;
        }

        var consumed = character.Inventory.ConsumeItem(null, ItemTaskType.DoodadInteraction, ItemId, required, null);
        if (consumed < required)
        {
            Logger.Error("DoodadFuncFeed: failed to consume {0}×{1} from {2} (consumed {3})", required, ItemId, character.Name, consumed);
            return;
        }

        Logger.Debug("DoodadFuncFeed: {0} fed {1}×{2} to doodad {3} (TemplateId {4}), nextPhase {5}",
            character.Name, consumed, ItemId, owner.ObjId, owner.TemplateId, nextPhase);

        // Canonical feed rows use next_phase = -1: feeding alone does not
        // advance the animal's phase. A chain that wires a next phase (e.g. a
        // fed → grown step) is honored here.
        owner.ToNextPhase = nextPhase > 0;
    }
}
