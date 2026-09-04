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

    [Test]
    public async Task CalculateGearScore_EvaluatesLevelGradeAndBrokenState()
    {
        var lowLevel = new Item
        {
            Id = 101,
            Grade = 1,
            Template = new ItemTemplate { Id = 101, Level = 10, LevelRequirement = 10 }
        };
        var highLevel = new Item
        {
            Id = 102,
            Grade = 1,
            Template = new ItemTemplate { Id = 102, Level = 20, LevelRequirement = 20 }
        };
        var highGrade = new Item
        {
            Id = 103,
            Grade = 4,
            Template = new ItemTemplate { Id = 103, Level = 10, LevelRequirement = 10 }
        };
        var brokenItem = new TestEquipItem(104, new EquipItemTemplate { Id = 104, Level = 50 }, 100)
        {
            Durability = 0
        };

        var scoreLow = BotBagManager.CalculateGearScore(lowLevel);
        var scoreHigh = BotBagManager.CalculateGearScore(highLevel);
        var scoreGrade = BotBagManager.CalculateGearScore(highGrade);
        var scoreBroken = BotBagManager.CalculateGearScore(brokenItem);

        await Assert.That(scoreHigh).IsGreaterThan(scoreLow);
        await Assert.That(scoreGrade).IsGreaterThan(scoreLow);
        await Assert.That(scoreBroken).IsEqualTo(0);
    }

    [Test]
    public async Task IsUpgrade_IdentifiesBetterGearAndRejectsUnderleveled()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("test-upgrade-eval");
        var character = session.Character;
        character.Level = 15;

        const uint equippedId = 90_041;
        const uint upgradeId = 90_042;
        const uint overleveledId = 90_043;
        const uint inferiorId = 90_044;

        GameplayActorTestRig.SeedEquipItemTemplate(equippedId, EquipmentItemSlotType.Mainhand, level: 10, levelRequirement: 10);
        GameplayActorTestRig.SeedEquipItemTemplate(upgradeId, EquipmentItemSlotType.Mainhand, level: 15, levelRequirement: 15);
        GameplayActorTestRig.SeedEquipItemTemplate(overleveledId, EquipmentItemSlotType.Mainhand, level: 25, levelRequirement: 25);
        GameplayActorTestRig.SeedEquipItemTemplate(inferiorId, EquipmentItemSlotType.Mainhand, level: 5, levelRequirement: 5);

        GameplayActorTestRig.StockItem(session, equippedId, 1);
        var equipReq = actor.Equip(equippedId);
        await Assert.That(equipReq.State).IsEqualTo(ActorLifecycleState.Completed);

        GameplayActorTestRig.StockItem(session, upgradeId, 1);
        GameplayActorTestRig.StockItem(session, overleveledId, 1);
        GameplayActorTestRig.StockItem(session, inferiorId, 1);

        var upgradeItem = character.Inventory.Bag.GetItemsSnapshot().First(i => i.TemplateId == upgradeId);
        var overleveledItem = character.Inventory.Bag.GetItemsSnapshot().First(i => i.TemplateId == overleveledId);
        var inferiorItem = character.Inventory.Bag.GetItemsSnapshot().First(i => i.TemplateId == inferiorId);

        await Assert.That(BotBagManager.IsUpgrade(character, upgradeItem, out var targetSlot)).IsTrue();
        await Assert.That(targetSlot).IsEqualTo(EquipmentItemSlot.Mainhand);
        await Assert.That(BotBagManager.IsUpgrade(character, overleveledItem, out _)).IsFalse();
        await Assert.That(BotBagManager.IsUpgrade(character, inferiorItem, out _)).IsFalse();
    }

    [Test]
    public async Task AutoEquipUpgrades_EquipsUpgradeAndDisplacesOldGear_MarkingDisplacedAsObsolete()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("test-autoequip");
        var character = session.Character;
        character.Level = 20;

        const uint oldSwordTemplateId = 90_031;
        const uint upgradeSwordTemplateId = 90_032;

        GameplayActorTestRig.SeedEquipItemTemplate(oldSwordTemplateId, EquipmentItemSlotType.Mainhand, level: 10, levelRequirement: 10);
        GameplayActorTestRig.SeedEquipItemTemplate(upgradeSwordTemplateId, EquipmentItemSlotType.Mainhand, level: 20, levelRequirement: 20);

        // Equip old sword into Mainhand
        GameplayActorTestRig.StockItem(session, oldSwordTemplateId, 1);
        var initialEquip = actor.Equip(oldSwordTemplateId);
        await Assert.That(initialEquip.State).IsEqualTo(ActorLifecycleState.Completed);

        var currentEquipped = character.Inventory.Equipment.GetItemBySlot((int)EquipmentItemSlot.Mainhand);
        await Assert.That(currentEquipped?.TemplateId).IsEqualTo(oldSwordTemplateId);

        // Stock upgrade sword into Bag
        GameplayActorTestRig.StockItem(session, upgradeSwordTemplateId, 1);
        await Assert.That(character.Inventory.Bag.GetItemsSnapshot().Any(i => i.TemplateId == upgradeSwordTemplateId)).IsTrue();

        // Act: AutoEquipUpgrades
        var (equippedCount, log) = BotBagManager.AutoEquipUpgrades(actor);

        await Assert.That(equippedCount).IsGreaterThan(0);
        await Assert.That(log.Count).IsGreaterThan(0);

        // Assert: Mainhand now has the upgrade sword
        var newlyEquipped = character.Inventory.Equipment.GetItemBySlot((int)EquipmentItemSlot.Mainhand);
        await Assert.That(newlyEquipped?.TemplateId).IsEqualTo(upgradeSwordTemplateId);

        // Assert: Old sword was displaced into Bag
        var displacedInBag = character.Inventory.Bag.GetItemsSnapshot().FirstOrDefault(i => i.TemplateId == oldSwordTemplateId);
        await Assert.That(displacedInBag).IsNotNull();

        // Assert: Displaced old sword is recognized as obsolete equipment and classified as trash for vendoring
        await Assert.That(BotBagManager.IsObsoleteEquipment(character, displacedInBag!)).IsTrue();
        var audit = BotBagManager.AuditBag(character);
        await Assert.That(audit.TrashItems.Any(i => i.TemplateId == oldSwordTemplateId)).IsTrue();
    }
}
