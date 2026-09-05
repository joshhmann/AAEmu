using System.Reflection;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Containers;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.UnitTests.Utils.Mocks;
using AAEmu.UnitTests.Game.Core.Managers.Bots;

namespace AAEmu.UnitTests.Game.Models.Game.Items.Containers;

/// <summary>
/// M7 equip-contract engine gap fix — level requirement gate on
/// EquipmentContainer.CanAccept (items.level_requirement from
/// compact.sqlite3, loaded into ItemTemplate.LevelRequirement by
/// ItemManager). The gate sits at the chokepoint both the client move path
/// (Inventory.SplitOrMoveItem) and service paths funnel through; a refusal
/// moves nothing and surfaces as SplitOrMoveItem == false (bot contract:
/// RejectedAction "refused by engine").
///
/// Two layers are covered here:
///  - direct CanAccept semantics (below/at/above requirement, 0 = no gate);
///  - the real equip path through GameplayActor.Equip → SplitOrMoveItem,
///    proving a below-level equip is refused with the bag untouched.
///
/// Fixture ids 90_04x are unique to this suite (the shared 9000x range is
/// claimed by other suites); each keeps ONE slot type process-wide.
/// </summary>
[ParallelLimiter<AAEmu.UnitTests.Game.Housing.SequentialParallelLimit>]
[NotInParallel]
public class EquipmentContainerLevelGateTests
{
    private const uint BelowLevelTemplateId = 90_041; // Mainhand-only
    private const uint AtLevelTemplateId = 90_042; // Mainhand-only
    private const uint NoRequirementTemplateId = 90_043; // Mainhand-only
    private const uint SwapBackTemplateId = 90_044; // Mainhand-only

    private static EquipmentContainer NewEquipmentContainer(CharacterMock character)
    {
        var container = new EquipmentContainer(character.Id, SlotType.Equipment, false, character);
        container.Owner = character; // public setter — avoids WorldManager lookups
        return container;
    }

    private static Item MockWeapon(uint itemId, uint templateId) => new ItemMock(itemId, new WeaponTemplate
    {
        Id = templateId,
        MaxCount = 1,
        HoldableTemplate = new Holdable { Id = templateId, SlotTypeId = (uint)EquipmentItemSlotType.Mainhand }
    });

    [Test]
    public async Task CanAccept_ItemBelowLevelRequirement_ReturnsFalse()
    {
        var character = new CharacterMock { Level = 5 };
        var container = NewEquipmentContainer(character);
        var item = MockWeapon(1, BelowLevelTemplateId);
        item.Template.LevelRequirement = 10;

        await Assert.That(container.CanAccept(item, (int)EquipmentItemSlot.Mainhand)).IsFalse();
    }

    [Test]
    public async Task CanAccept_ItemAtOrAboveLevelRequirement_ReturnsTrue()
    {
        var character = new CharacterMock { Level = 10 };
        var container = NewEquipmentContainer(character);
        var item = MockWeapon(2, AtLevelTemplateId);
        item.Template.LevelRequirement = 10;

        await Assert.That(container.CanAccept(item, (int)EquipmentItemSlot.Mainhand)).IsTrue();

        var higher = new CharacterMock { Level = 55 };
        var higherContainer = NewEquipmentContainer(higher);
        await Assert.That(higherContainer.CanAccept(item, (int)EquipmentItemSlot.Mainhand)).IsTrue();
    }

    [Test]
    public async Task CanAccept_ItemWithoutLevelRequirement_AnyLevel()
    {
        var item = MockWeapon(3, NoRequirementTemplateId);
        item.Template.LevelRequirement = 0;

        foreach (var level in new byte[] { 1, 10, 55 })
        {
            var container = NewEquipmentContainer(new CharacterMock { Level = level });
            await Assert.That(container.CanAccept(item, (int)EquipmentItemSlot.Mainhand)).IsTrue();
        }
    }

    [Test]
    public async Task CanAccept_UnequipAndWrongSlot_BehaviorUnchanged()
    {
        // Regression: un-equip (null item) is still always allowed, and the
        // existing slot-compatibility refusals still fire before the level
        // gate matters.
        var character = new CharacterMock { Level = 1 };
        var container = NewEquipmentContainer(character);

        await Assert.That(container.CanAccept(null, (int)EquipmentItemSlot.Mainhand)).IsTrue();

        var item = MockWeapon(4, SwapBackTemplateId);
        await Assert.That(container.CanAccept(item, (int)EquipmentItemSlot.Chest)).IsFalse();
    }

    private static void SeedLevelGatedEquipTemplate(uint templateId, int levelRequirement)
    {
        GameplayActorTestRig.SeedEquipItemTemplate(templateId, EquipmentItemSlotType.Mainhand);
        var templates = (Dictionary<uint, ItemTemplate>)typeof(ItemManager)
            .GetField("_templates", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(ItemManager.Instance)!;
        templates[templateId].LevelRequirement = levelRequirement;
    }

    [Test]
    public async Task Equip_BelowLevelRequirement_RejectedByEngine_NothingMoves()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("m7-equip-level-1");
        actor.Character.Level = 5;
        SeedLevelGatedEquipTemplate(BelowLevelTemplateId, 10);
        GameplayActorTestRig.StockItem(session, BelowLevelTemplateId, 1);

        var request = actor.Equip(BelowLevelTemplateId);

        // The engine's refusal surfaces as the standard Rejected mapping.
        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(request.Detail?.Contains("refused by engine")).IsTrue();
        // Inventory unchanged: nothing equipped, item still bagged.
        await Assert.That(actor.Character.Inventory.Equipment.GetItemBySlot((int)EquipmentItemSlot.Mainhand)).IsNull();
        actor.Character.Inventory.Bag.GetAllItemsByTemplate(BelowLevelTemplateId, -1, out var stillBagged, out _);
        await Assert.That(stillBagged.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Equip_AtExactLevelRequirement_Completes()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("m7-equip-level-2");
        actor.Character.Level = 10;
        SeedLevelGatedEquipTemplate(AtLevelTemplateId, 10);
        GameplayActorTestRig.StockItem(session, AtLevelTemplateId, 1);

        var request = actor.Equip(AtLevelTemplateId);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Completed);
        var equipped = actor.Character.Inventory.Equipment.GetItemBySlot((int)EquipmentItemSlot.Mainhand);
        await Assert.That(equipped).IsNotNull();
        await Assert.That(equipped!.TemplateId).IsEqualTo(AtLevelTemplateId);
        await Assert.That(actor.Character.Inventory.Bag.GetItemByItemId(equipped.Id)).IsNull();
    }
}
