using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Game.Items.Containers;
using AAEmu.UnitTests.Utils;

namespace AAEmu.UnitTests.Game.Models.Game.Items.Containers;

/// <summary>
/// Boot-restore bypass on <see cref="ItemContainer.AddOrMoveExistingItem"/>:
/// LoadUserItems places persisted state verbatim (skipValidation: true),
/// deferring live CanAccept/ParentUnit gates to runtime. Runtime adds
/// (default false) stay fail-closed — e.g. an orphaned MateEquipmentContainer
/// (no ParentUnit at boot) still refuses.
/// </summary>
public class BootRestoreBypassTests
{
    [Test]
    public async Task RuntimeAdd_OrphanedMateContainer_RefusedFailClosed()
    {
        var container = new MateEquipmentContainer(0, SlotType.EquipmentMate, false, null);
        var item = InventoryTestUtils.MockItem(1, 23092);
        item.Slot = 5;

        await Assert.That(container.AddOrMoveExistingItem(ItemTaskType.Invalid, item, item.Slot)).IsFalse();
        await Assert.That(item._holdingContainer).IsNull();
    }

    [Test]
    public async Task BootRestoreAdd_OrphanedMateContainer_PlacedVerbatim()
    {
        var container = new MateEquipmentContainer(0, SlotType.EquipmentMate, false, null);
        var item = InventoryTestUtils.MockItem(2, 23092);
        item.Slot = 5;

        await Assert.That(container.AddOrMoveExistingItem(ItemTaskType.Invalid, item, item.Slot, true)).IsTrue();
        await Assert.That(item._holdingContainer).IsEqualTo(container);
        await Assert.That(item.Slot).IsEqualTo(5);
        await Assert.That(container.GetItemBySlot(5)).IsEqualTo(item);
    }
}
