using System.Numerics;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Game.Items.Containers;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.NPChar;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

[NotInParallel]
public class BotBagManagerTests
{
    [Test]
    public async Task IsTrash_CorrectlyIdentifiesTrashAndProtectsValuables()
    {
        // 1. Trash category item
        var trashItem = new Item
        {
            Id = 1001,
            Template = new ItemTemplate
            {
                Id = 5001,
                CategoryId = (int)ItemCategory.Trash_Miscellaneous,
                Sellable = true,
                Refund = 50
            }
        };
        await Assert.That(BotBagManager.IsTrash(trashItem)).IsTrue();

        // 2. Quest item (MUST NOT be sold)
        var questItem = new Item
        {
            Id = 1002,
            Template = new ItemTemplate
            {
                Id = 5002,
                CategoryId = (int)ItemCategory.Quest_Item,
                Sellable = true,
                Refund = 100
            }
        };
        await Assert.That(BotBagManager.IsTrash(questItem)).IsFalse();

        // 3. LootQuest item (MUST NOT be sold)
        var lootQuestItem = new Item
        {
            Id = 1003,
            Template = new ItemTemplate
            {
                Id = 5003,
                CategoryId = (int)ItemCategory.Other,
                LootQuestId = 250,
                Sellable = true,
                Refund = 20
            }
        };
        await Assert.That(BotBagManager.IsTrash(lootQuestItem)).IsFalse();

        // 4. Essential Potion (MUST NOT be sold)
        var potionItem = new Item
        {
            Id = 1004,
            Template = new ItemTemplate
            {
                Id = 5004,
                CategoryId = (int)ItemCategory.Potion,
                Sellable = true,
                Refund = 10
            }
        };
        await Assert.That(BotBagManager.IsTrash(potionItem)).IsFalse();

        // 5. Unsellable item
        var unsellableItem = new Item
        {
            Id = 1005,
            Template = new ItemTemplate
            {
                Id = 5005,
                CategoryId = (int)ItemCategory.Trash_Miscellaneous,
                Sellable = false,
                Refund = 0
            }
        };
        await Assert.That(BotBagManager.IsTrash(unsellableItem)).IsFalse();
    }

    [Test]
    public async Task AuditBag_CalculatesCapacityAndIdentifiesTrash()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("test-bag-audit");
        var character = session.Character;

        // Verify initial state
        var auditInitial = BotBagManager.AuditBag(character);
        await Assert.That(auditInitial.TotalCapacity).IsGreaterThan(0);
        await Assert.That(auditInitial.FreeSlots).IsEqualTo(auditInitial.TotalCapacity);
        await Assert.That(auditInitial.IsNearFull).IsFalse();

        // Add a trash item
        var trashItem = new Item
        {
            Id = 9901,
            Template = new ItemTemplate
            {
                Id = 9901,
                CategoryId = (int)ItemCategory.Trash_Miscellaneous,
                Sellable = true,
                Refund = 120,
                MaxCount = 100
            },
            Count = 3
        };
        character.Inventory.Bag.AddOrMoveExistingItem(ItemTaskType.Loot, trashItem);

        var auditAfter = BotBagManager.AuditBag(character);
        await Assert.That(auditAfter.TrashItems.Count).IsEqualTo(1);
        await Assert.That(auditAfter.TotalTrashEstimatedValue).IsEqualTo(360); // 120 * 3
        await Assert.That(auditAfter.UsedSlots).IsEqualTo(1);
    }

    [Test]
    public async Task SellAllTrash_SellsJunkToMerchant_FreesSlotsAndEarnsMoney()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("test-bag-seller");
        var character = session.Character;
        character.Money = 500;

        // Spawn a merchant NPC near the character
        var merchantObjId = GameplayActorTestRig.SpawnMerchantNpc(session, npcTemplateId: 7701);
        var merchant = session.World.GetNpc(merchantObjId)!;
        merchant.Transform.World.Position = character.Transform.World.Position;

        // Add 2 trash items and 1 potion to the bag
        var junk1 = new Item
        {
            Id = 8801,
            Template = new ItemTemplate
            {
                Id = 8801,
                CategoryId = (int)ItemCategory.Trash_Miscellaneous,
                Sellable = true,
                Refund = 100,
                MaxCount = 100
            },
            Count = 2
        };
        var junk2 = new Item
        {
            Id = 8802,
            Template = new ItemTemplate
            {
                Id = 8802,
                CategoryId = (int)ItemCategory.Trash_Craft,
                Sellable = true,
                Refund = 50,
                MaxCount = 100
            },
            Count = 1
        };
        var potion = new Item
        {
            Id = 8803,
            Template = new ItemTemplate
            {
                Id = 8803,
                CategoryId = (int)ItemCategory.Potion,
                Sellable = true,
                Refund = 30,
                MaxCount = 100
            },
            Count = 5
        };

        character.Inventory.Bag.AddOrMoveExistingItem(ItemTaskType.Loot, junk1);
        character.Inventory.Bag.AddOrMoveExistingItem(ItemTaskType.Loot, junk2);
        character.Inventory.Bag.AddOrMoveExistingItem(ItemTaskType.Loot, potion);

        await Assert.That(character.Inventory.Bag.GetItemsSnapshot().Count).IsEqualTo(3);

        // Act: Sell all trash
        var (soldCount, revenue) = BotBagManager.SellAllTrash(actor, merchantObjId);

        await Assert.That(soldCount).IsEqualTo(2);
        await Assert.That(revenue).IsGreaterThan(0);
        await Assert.That(character.Money).IsGreaterThan(500);

        // Verify potion remains in bag, junk is gone
        var remaining = character.Inventory.Bag.GetItemsSnapshot();
        await Assert.That(remaining.Count).IsEqualTo(1);
        await Assert.That(remaining[0].Id).IsEqualTo(8803ul);
    }

    private sealed class TestEquipItem : EquipItem
    {
        private readonly byte _maxDurability;
        public override byte MaxDurability => _maxDurability;

        public TestEquipItem(ulong id, EquipItemTemplate template, byte maxDurability) : base(id, template, 1)
        {
            _maxDurability = maxDurability;
        }
    }

    [Test]
    public async Task RepairAllEquipment_RestoresDurabilityAtBlacksmith()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("test-bag-repair");
        var character = session.Character;
        character.Money = 10_000;

        var blacksmithObjId = GameplayActorTestRig.SpawnBlacksmithNpc(session, npcTemplateId: 8805);
        var blacksmith = session.World.GetNpc(blacksmithObjId)!;
        character.Transform.World.Position = blacksmith.Transform.World.Position;

        // Seed equippable item with reduced durability in equipment container
        var equipTemplate = new WeaponTemplate
        {
            Id = 9101,
            CategoryId = (int)ItemCategory.Sword,
            Sellable = true,
            Price = 100,
            HoldableTemplate = new Holdable { Id = 9101, SlotTypeId = (uint)EquipmentItemSlotType.OneHanded }
        };
        var equip = new TestEquipItem(9991, equipTemplate, 50)
        {
            Durability = 5
        };
        character.Inventory.Equipment.AddOrMoveExistingItem(ItemTaskType.Loot, equip, (int)EquipmentItemSlot.Mainhand);

        await Assert.That(equip.Durability).IsLessThan(equip.MaxDurability);

        // Act: repair
        var (repairedCount, cost) = BotBagManager.RepairAllEquipment(actor, blacksmithObjId);

        await Assert.That(repairedCount).IsEqualTo(1);
        await Assert.That(equip.Durability).IsEqualTo(equip.MaxDurability);
    }
}
