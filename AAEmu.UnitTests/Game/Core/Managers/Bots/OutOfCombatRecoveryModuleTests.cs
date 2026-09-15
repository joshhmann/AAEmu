using System.Numerics;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Game.Items.Containers;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.Units;
using AAEmu.UnitTests.Utils.Mocks;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

[NotInParallel]
public class OutOfCombatRecoveryModuleTests
{
    private static Character CreateTestCharacter()
    {
        OutOfCombatRecoveryModule.ResetState();
        var (actor, session) = GameplayActorTestRig.CreateActor($"recovery-test-{Guid.NewGuid():N}");
        var character = session.Character;
        character.Hp = character.MaxHp;
        character.Mp = character.MaxMp;
        character.IsInBattle = false;
        character.Stance = UnitStance.Stand;
        return character;
    }

    [Test]
    public async Task TriggerEvaluation_TriggersAtLowHpWhenOutOfCombat()
    {
        var character = CreateTestCharacter();

        // 70% HP is below 75% trigger threshold
        character.Hp = (int)(character.MaxHp * 0.70f);
        character.Mp = character.MaxMp;
        character.IsInBattle = false;

        var shouldTrigger = OutOfCombatRecoveryModule.ShouldTriggerRecovery(character);
        await Assert.That(shouldTrigger).IsTrue();
    }

    [Test]
    public async Task TriggerEvaluation_TriggersAtLowMpWhenOutOfCombat()
    {
        var character = CreateTestCharacter();

        // 50% MP is below 60% trigger threshold
        character.Hp = character.MaxHp;
        character.Mp = (int)(character.MaxMp * 0.50f);
        character.IsInBattle = false;

        var shouldTrigger = OutOfCombatRecoveryModule.ShouldTriggerRecovery(character);
        await Assert.That(shouldTrigger).IsTrue();
    }

    [Test]
    public async Task TriggerEvaluation_IgnoresWhenInCombatEvenAtLowHealthAndMana()
    {
        var character = CreateTestCharacter();

        // Very low HP and MP, but in combat
        character.Hp = (int)(character.MaxHp * 0.20f);
        character.Mp = (int)(character.MaxMp * 0.20f);
        character.IsInBattle = true;

        var shouldTrigger = OutOfCombatRecoveryModule.ShouldTriggerRecovery(character);
        await Assert.That(shouldTrigger).IsFalse();
    }

    [Test]
    public async Task TriggerEvaluation_IgnoresWhenFullHealthAndMana()
    {
        var character = CreateTestCharacter();

        character.Hp = character.MaxHp;
        character.Mp = character.MaxMp;
        character.IsInBattle = false;

        var shouldTrigger = OutOfCombatRecoveryModule.ShouldTriggerRecovery(character);
        await Assert.That(shouldTrigger).IsFalse();
    }

    [Test]
    public async Task TriggerEvaluation_IgnoresWhenHealthAndManaAboveThresholds()
    {
        var character = CreateTestCharacter();

        // 80% HP (> 75%) and 80% MP (> 60%)
        character.Hp = (int)(character.MaxHp * 0.80f);
        character.Mp = (int)(character.MaxMp * 0.80f);
        character.IsInBattle = false;

        var shouldTrigger = OutOfCombatRecoveryModule.ShouldTriggerRecovery(character);
        await Assert.That(shouldTrigger).IsFalse();
    }

    [Test]
    public async Task FoodItemSelection_SelectsEdibleItemsFromInventory()
    {
        var character = CreateTestCharacter();
        var bag = character.Inventory.Bag;

        // 1. Add non-edible trash weapon
        var sword = new Item
        {
            Id = 7001,
            Template = new ItemTemplate
            {
                Id = 7001,
                Name = "Rusty Iron Sword",
                CategoryId = (int)ItemCategory.Sword,
                Sellable = true
            }
        };
        bag.AddOrMoveExistingItem(ItemTaskType.Loot, sword);

        // 2. Add Bread (Food)
        var bread = new Item
        {
            Id = 7002,
            Template = new ItemTemplate
            {
                Id = 17664,
                Name = "Hard Bread",
                CategoryId = (int)ItemCategory.Food,
                UseSkillId = 14437
            },
            Count = 5
        };
        bag.AddOrMoveExistingItem(ItemTaskType.Loot, bread);

        // 3. Add Soup (Drink)
        var soup = new Item
        {
            Id = 7003,
            Template = new ItemTemplate
            {
                Id = 17668,
                Name = "Vegetable Soup",
                CategoryId = (int)ItemCategory.Drink,
                UseSkillId = 14443
            },
            Count = 3
        };
        bag.AddOrMoveExistingItem(ItemTaskType.Loot, soup);

        // 4. Add Cooked Potato Dish (Food)
        var potatoDish = new Item
        {
            Id = 7004,
            Template = new ItemTemplate
            {
                Id = 14586,
                Name = "Cooked Potato Stew",
                CategoryId = (int)ItemCategory.Food,
                UseSkillId = 12232
            },
            Count = 2
        };
        bag.AddOrMoveExistingItem(ItemTaskType.Loot, potatoDish);

        // Low HP -> Prioritizes HP food (Bread or Potato dish)
        character.Hp = (int)(character.MaxHp * 0.50f);
        character.Mp = character.MaxMp;
        var selectedForHp = OutOfCombatRecoveryModule.SelectConsumableItem(character);
        await Assert.That(selectedForHp).IsNotNull();
        await Assert.That(selectedForHp!.Template.CategoryId == (int)ItemCategory.Food).IsTrue();

        // Low MP -> Prioritizes Drink / Soup
        character.Hp = character.MaxHp;
        character.Mp = (int)(character.MaxMp * 0.50f);
        var selectedForMp = OutOfCombatRecoveryModule.SelectConsumableItem(character);
        await Assert.That(selectedForMp).IsNotNull();
        await Assert.That(selectedForMp!.Template.CategoryId == (int)ItemCategory.Drink).IsTrue();
    }

    [Test]
    public async Task FoodItemSelection_ReturnsNullWhenNoConsumablesInBag()
    {
        var character = CreateTestCharacter();
        var bag = character.Inventory.Bag;

        var junk = new Item
        {
            Id = 8001,
            Template = new ItemTemplate
            {
                Id = 8001,
                Name = "Small Rock",
                CategoryId = (int)ItemCategory.Raw_Stone
            }
        };
        bag.AddOrMoveExistingItem(ItemTaskType.Loot, junk);

        character.Hp = (int)(character.MaxHp * 0.50f);
        character.Mp = (int)(character.MaxMp * 0.50f);

        var selected = OutOfCombatRecoveryModule.SelectConsumableItem(character);
        await Assert.That(selected).IsNull();
    }

    [Test]
    public async Task SittingStanceTransition_EntersSitStanceAndAppliesMultiplier()
    {
        var character = CreateTestCharacter();
        character.Stance = UnitStance.Stand;

        // Verify initial standing stance
        await Assert.That(character.Stance).IsEqualTo(UnitStance.Stand);
        var standingHpRegen = character.HpRegen;

        // Enter sitting stance
        var sat = OutOfCombatRecoveryModule.EnterSittingStance(character);
        await Assert.That(sat).IsTrue();
        await Assert.That(character.Stance).IsEqualTo(UnitStance.Sit);
        await Assert.That(character.IdleStatus).IsTrue();

        // Verify natural sitting regen multiplier is 2.0x
        var sittingHpRegen = character.HpRegen;
        await Assert.That(sittingHpRegen).IsEqualTo(standingHpRegen * 2);
    }

    [Test]
    public async Task SittingStanceTransition_ExitsToStandingWhenRecoveryComplete()
    {
        var character = CreateTestCharacter();

        // Put bot in sitting stance for recovery
        OutOfCombatRecoveryModule.EnterSittingStance(character);
        await Assert.That(character.Stance).IsEqualTo(UnitStance.Sit);

        // Still below 95%: recovery not complete
        character.Hp = (int)(character.MaxHp * 0.90f); // 90%
        character.Mp = (int)(character.MaxMp * 0.90f); // 90%
        await Assert.That(OutOfCombatRecoveryModule.IsRecoveryComplete(character)).IsFalse();

        // Reaches 96% HP and 96% MP: recovery complete
        character.Hp = (int)(character.MaxHp * 0.96f); // 96%
        character.Mp = (int)(character.MaxMp * 0.96f); // 96%
        await Assert.That(OutOfCombatRecoveryModule.IsRecoveryComplete(character)).IsTrue();

        // Stand up on exit
        var stood = OutOfCombatRecoveryModule.ExitSittingStance(character);
        await Assert.That(stood).IsTrue();
        await Assert.That(character.Stance).IsEqualTo(UnitStance.Stand);
        await Assert.That(character.IdleStatus).IsFalse();
    }

    [Test]
    public async Task ExecuteRecoveryStep_FullLifecycle_FromFoodConsumptionToSitAndComplete()
    {
        OutOfCombatRecoveryModule.ResetState();
        var (actor, session) = GameplayActorTestRig.CreateActor($"recovery-cycle-{Guid.NewGuid():N}");
        var character = session.Character;
        character.Hp = (int)(character.MaxHp * 0.50f); // 50% HP
        character.Mp = (int)(character.MaxMp * 0.40f); // 40% MP
        character.IsInBattle = false;

        // Add cooked potato dish (Two-Potatoes loop closure)
        var potatoDish = new Item
        {
            Id = 9101,
            Template = new ItemTemplate
            {
                Id = 14586,
                Name = "Cooked Potato Bread",
                CategoryId = (int)ItemCategory.Food,
                UseSkillId = 12232
            },
            Count = 1
        };
        character.Inventory.Bag.AddOrMoveExistingItem(ItemTaskType.Loot, potatoDish);

        // Step 1: Triggers recovery and consumes food, entering sit stance
        var initialHp = character.Hp;
        var step1 = OutOfCombatRecoveryModule.ExecuteRecoveryStep(character, actor);
        await Assert.That(step1.Status == RecoveryStatus.Eating || step1.Status == RecoveryStatus.Resting).IsTrue();
        await Assert.That(character.Stance).IsEqualTo(UnitStance.Sit);

        // Step 2: In sitting stance, natural sitting regen ticks
        OutOfCombatRecoveryModule.ApplySittingRegenTick(character);
        await Assert.That(character.Hp).IsGreaterThan(initialHp);

        // Advance health to 96% and mana to 96%
        character.Hp = (int)(character.MaxHp * 0.96f);
        character.Mp = (int)(character.MaxMp * 0.96f);

        // Step 3: Exit condition met -> transitions to standing and marks complete
        var step3 = OutOfCombatRecoveryModule.ExecuteRecoveryStep(character, actor);
        await Assert.That(step3.Status).IsEqualTo(RecoveryStatus.Completed);
        await Assert.That(character.Stance).IsEqualTo(UnitStance.Stand);
        await Assert.That(character.IdleStatus).IsFalse();
    }

    [Test]
    public async Task ActivityModule_CanActivate_HonorsHysteresisAndCombatGates()
    {
        OutOfCombatRecoveryModule.ResetState();
        var (actor, session) = GameplayActorTestRig.CreateActor($"recovery-arbiter-{Guid.NewGuid():N}");
        var character = session.Character;
        character.Hp = character.MaxHp;
        character.Mp = character.MaxMp;
        character.IsInBattle = false;

        var runtime = new PlayerBotRuntime(character, "recovery-tests");

        var module = new OutOfCombatRecoveryModule();

        // 1. Healthy bot: denied
        var contextHealthy = new BotActivityContext
        {
            Bot = runtime,
            GameHour = 12.0f,
            ActiveActivity = null
        };
        var decisionHealthy = module.CanActivate(contextHealthy);
        await Assert.That(decisionHealthy.CanActivate).IsFalse();

        // 2. Wounded bot out of combat: allowed (50% HP < 75%)
        character.Hp = (int)(character.MaxHp * 0.50f);
        var decisionWounded = module.CanActivate(contextHealthy);
        await Assert.That(decisionWounded.CanActivate).IsTrue();
        await Assert.That(decisionWounded.ActivityName).IsEqualTo(OutOfCombatRecoveryModule.ActivityNameRest);

        // 3. Wounded bot engaged in combat: denied
        character.IsInBattle = true;
        var decisionCombat = module.CanActivate(contextHealthy);
        await Assert.That(decisionCombat.CanActivate).IsFalse();

        // 4. Out of combat again, enters sit stance
        character.IsInBattle = false;
        character.Stance = UnitStance.Sit;

        // In recovery at 80% HP (above trigger 75%, but below exit 95%): stays allowed (hysteresis)
        character.Hp = (int)(character.MaxHp * 0.80f);
        var contextRecovering = new BotActivityContext
        {
            Bot = runtime,
            GameHour = 12.0f,
            ActiveActivity = OutOfCombatRecoveryModule.ActivityNameRest
        };
        var decisionRecovering = module.CanActivate(contextRecovering);
        await Assert.That(decisionRecovering.CanActivate).IsTrue();

        // 5. Fully recovered (96%): stands up and denies
        character.Hp = (int)(character.MaxHp * 0.96f);
        character.Mp = (int)(character.MaxMp * 0.96f);
        var decisionComplete = module.CanActivate(contextRecovering);
        await Assert.That(decisionComplete.CanActivate).IsFalse();
        await Assert.That(character.Stance).IsEqualTo(UnitStance.Stand);
    }
}
