using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;

namespace AAEmu.Game.Models.Game.DoodadObj.Funcs;

public class DoodadFuncLootItem : DoodadFuncTemplate
{
    // doodad_funcs
    // ReSharper disable once UnusedAutoPropertyAccessor.Global
    public WorldInteractionType WorldInteractionId { get; set; }
    public uint ItemId { get; init; }
    public int CountMin { get; init; }
    public int CountMax { get; init; }
    public int Percent { get; init; }
    public int RemainTime { get; init; }
    public uint GroupId { get; init; }

    public override void Use(BaseUnit caster, Doodad owner, uint skillId, int nextPhase = 0)
    {
        if (caster is Character)
            Logger.Debug($"DoodadFuncLootItem: skillId {skillId}, nextPhase {nextPhase},  ItemId {ItemId}, CountMin {CountMin}, CountMax {CountMax},  Percent {Percent}, RemainTime {RemainTime}, GroupId {GroupId}");
        else
            Logger.Trace($"DoodadFuncLootItem: skillId {skillId}, nextPhase {nextPhase},  ItemId {ItemId}, CountMin {CountMin}, CountMax {CountMax},  Percent {Percent}, RemainTime {RemainTime}, GroupId {GroupId}");

        var character = (Character)caster;
        var res = true;
        if (character == null)
            return;

        // Trace (consequence state, schema v2): measure the bag before the
        // grant so the loot row can carry the exact item-unit delta. Null when
        // the container is unreadable (explicit unknown).
        var traceBagBefore = PlayerTraceService.Instance.IsActive ? PlayerTraceService.BagItemUnits(character) : null;

        var chance = Random.Shared.Next(0, 10000);
        if (chance > Percent)
            return;

        var count = Random.Shared.Next(CountMin, CountMax);

        if (ItemId == 500)
        {
            character.Money += count;
            res = character.AddMoney(SlotType.Inventory, count);
        }
        else
        {
            if (ItemManager.Instance.IsAutoEquipTradePack(ItemId))
            {
                var item = ItemManager.Instance.Create(ItemId, count, 0);
                if (character.Inventory.TakeoffBackpack(ItemTaskType.RecoverDoodadItem, true))
                {
                    res = character.Inventory.Equipment.AddOrMoveExistingItem(ItemTaskType.RecoverDoodadItem, item,
                        (int)EquipmentItemSlot.Backpack);
                }
            }
            else
            {
                res = character.Inventory.Bag.AcquireDefaultItem(ItemTaskType.RecoverDoodadItem, ItemId, count);
            }
        }

        if (res == false)
            character.SendErrorMessage(ErrorMessageType.BagInvalidItem);

        // Trace (consequence state, schema v2): record the doodad loot outcome
        // with its grant result (res), the exact bag delta of the grant, and the
        // post-grant snapshot. Coins (ItemId 500) land in money, so their bag
        // delta stays null. Ordinary accessors only; tracing mutates nothing.
        if (PlayerTraceService.Instance.IsActive)
        {
            var bagAfter = PlayerTraceService.BagItemUnits(character);
            var bagDelta = res && ItemId != Item.Coins && traceBagBefore.HasValue && bagAfter.HasValue
                ? bagAfter.Value - traceBagBefore.Value
                : (int?)null;
            PlayerTraceService.Instance.RecordLoot(character, "loot_granted", owner.ObjId, ItemId, res ? count : 0, bagDelta, new
            {
                Source = "DoodadFuncLootItem",
                DoodadTemplateId = owner.TemplateId,
                Granted = res,
                RollPercent = Percent,
                GroupId
            });
        }

        // Move to next phase only when loot was actually granted.
        owner.ToNextPhase = res;
    }
}
