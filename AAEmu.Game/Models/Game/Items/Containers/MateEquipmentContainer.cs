using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.Game.Models.Game.Items.Containers;

public class MateEquipmentContainer : EquipmentContainer
{
    public MateEquipmentContainer(uint ownerId, SlotType containerType, bool createWithNewId, Unit parentUnit) : base(ownerId, containerType, createWithNewId, parentUnit)
    {
        // Fancy way of getting the last enum value + 1 for equipment slots
        ContainerSize = (int)Enum.GetValues<EquipmentItemSlot>().Max() + 1;
    }

    public override bool CanAccept(Item item, int targetSlot)
    {
        if (item == null)
            return true; // always allow empty item slot (un-equip a item)

        // Fail closed: this container must belong to a Mate to accept equipment
        if (ParentUnit is not Units.Mate mate)
            return false;

        if (targetSlot < 0 || targetSlot >= ContainerSize)
        {
            Logger.Warn($"{Owner?.Name ?? mate.Template?.Id.ToString() ?? "?"} ({OwnerId}) tried to equip a item that is out of range of the valid slots {targetSlot}/{ContainerSize}");
            return false;
        }

        // Mate legality gate from the mate_equip_* tables:
        // the item's template must be bound to one of this mate's packs (fail closed
        // when either side has no data), and the mate's slot pack must allow the
        // targeted equipment slot.
        var allowed = MateGameData.Instance.IsMateEquipAllowed(
            mate.Template.Id, mate.Template.MateEquipSlotPackId,
            item.TemplateId, (EquipmentItemSlot)targetSlot);

        if (!allowed)
        {
            Logger.Warn($"{Owner?.Name ?? "Unknown"} ({OwnerId}) tried to equip a illegal mate equipment {item.Template?.Name} ({item.TemplateId}) on mate npc {mate.Template.Id}, TargetSlot:{(EquipmentItemSlot)targetSlot}");
            return false;
        }

        // Level requirement gate (same pattern as EquipmentContainer.CanAccept).
        // Mate containers normally have no direct Owner character, which makes
        // base.CanAccept short-circuit before its checks, so evaluate it against
        // the mate's owner character instead.
        if (Owner != null)
            return base.CanAccept(item, targetSlot);

        var ownerChar = mate.GetOwnerCharacter();
        if (ownerChar != null && item.Template is { LevelRequirement: > 0 } template && template.LevelRequirement > ownerChar.Level)
        {
            Logger.Warn($"{ownerChar.Name} ({ownerChar.Id}) tried to equip a item above their level on their mate {item.Template.Name} ({item.TemplateId}), Id:{item.Id}, RequiredLevel:{template.LevelRequirement}, CharacterLevel:{ownerChar.Level}, TargetSlot:{(EquipmentItemSlot)targetSlot}");
            return false;
        }

        return true;
    }

    public override void OnEnterContainer(Item item, ItemContainer lastContainer, byte previousSlot)
    {
        base.OnEnterContainer(item, lastContainer, previousSlot); // base EquipmentContainer

        // Extra pockets for mates
        if (ParentUnit is not Units.Mate mate)
        {
            return;
        }

        var petItem = new ItemAndLocation
        {
            Item = item,
            SlotType = lastContainer.ContainerType, // ContainerType,
            SlotNumber = previousSlot,
        };
        var inventoryItem = new ItemAndLocation
        {
            Item = null,
            SlotType = ContainerType,
            SlotNumber = (byte)item.Slot,
        };
        // Owner.SendMessage($"MateEquipmentContainer - {petItem} -> {inventoryItem}, MateTl: {mate.TlId}");
        Owner.SendPacket(new SCMateEquipmentChangedPacket(petItem, inventoryItem, mate.TlId, Owner.Id, 0, false, true));
    }

    public override void OnLeaveContainer(Item item, ItemContainer newContainer, byte previousSlot)
    {
        base.OnLeaveContainer(item, newContainer, previousSlot); // base EquipmentContainer

        // Extra pockets for mates
        if (ParentUnit is not Units.Mate mate)
        {
            return;
        }

        var petItem = new ItemAndLocation
        {
            Item = null,
            SlotType = item.SlotType, // newContainer
            SlotNumber = (byte)item.Slot,
        };
        var inventoryItem = new ItemAndLocation
        {
            Item = item,
            SlotType = ContainerType,
            SlotNumber = previousSlot,
        };
        // Owner.SendMessage($"MateEquipmentContainer - {petItem} -> {inventoryItem}, MateTl: {mate.TlId}");
        Owner.SendPacket(new SCMateEquipmentChangedPacket(petItem, inventoryItem, mate.TlId, Owner.Id, 0, false, true));
    }
}
