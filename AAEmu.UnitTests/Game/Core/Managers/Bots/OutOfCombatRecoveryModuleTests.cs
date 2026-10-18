using System.Reflection;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Quests.Acts;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.Game.Models.Game.Quests.Templates;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Effects;
using AAEmu.Game.Models.Game.Skills.Effects.Enums;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

[NotInParallel]
public class OutOfCombatRecoveryModuleTests
{
    [Before(Test)]
    public void SetUp() => ExecutionBoundary.SetExecutionThreadForTest(Environment.CurrentManagedThreadId);

    [Test]
    public async Task ExecuteRecoveryStep_WithoutSupply_DoesNotAdvanceNativeVitals()
    {
        var actor = OrdinaryRecoveryTestRig.CreateActor("recovery-no-supply");
        var character = actor.Character;
        var hp = character.Hp;
        var mp = character.Mp;

        var result = OutOfCombatRecoveryModule.ExecuteRecoveryStep(character);

        await Assert.That(result.Status).IsEqualTo(RecoveryStatus.Resting);
        await Assert.That(character.Hp).IsEqualTo(hp);
        await Assert.That(character.Mp).IsEqualTo(mp);
        await Assert.That(character.Stance).IsEqualTo(UnitStance.Sit);
        OutOfCombatRecoveryModule.ExitSittingStance(character);
    }

    [Test]
    public async Task ExecuteRecoveryStep_UnusableFood_DoesNotConsumeOrGrantPoints()
    {
        var actor = OrdinaryRecoveryTestRig.CreateActor("recovery-refused-food");
        var character = actor.Character;
        var food = new Item
        {
            Id = 99881,
            Template = new ItemTemplate { Id = 99881, Name = "Bread", CategoryId = (int)ItemCategory.Food },
            Count = 2
        };
        character.Inventory.Bag.AddOrMoveExistingItem(ItemTaskType.Loot, food);
        var hp = character.Hp;
        var mp = character.Mp;

        OutOfCombatRecoveryModule.ExecuteRecoveryStep(character, actor);

        await Assert.That(food.Count).IsEqualTo(2);
        await Assert.That(character.Hp).IsEqualTo(hp);
        await Assert.That(character.Mp).IsEqualTo(mp);
        await Assert.That(actor.AuditTrace.Any(r => r.Action == ActorActionType.UseItem)).IsFalse();
        OutOfCombatRecoveryModule.ExitSittingStance(character);
    }

    [Test]
    [Arguments(0.70f, 1f, false, true)]
    [Arguments(1f, 0.50f, false, true)]
    [Arguments(0.20f, 0.20f, true, false)]
    [Arguments(1f, 1f, false, false)]
    [Arguments(0.80f, 0.80f, false, false)]
    public async Task TriggerEvaluation_KeepsExistingThresholds(float hp, float mp, bool inBattle, bool expected)
    {
        var actor = OrdinaryRecoveryTestRig.CreateActor("recovery-threshold", hp, mp);
        actor.Character.IsInBattle = inBattle;
        await Assert.That(OutOfCombatRecoveryModule.ShouldTriggerRecovery(actor.Character)).IsEqualTo(expected);
    }

    [Test]
    public async Task Selection_UsesOrdinaryEffects_NotNamesOrCategories()
    {
        var actor = OrdinaryRecoveryTestRig.CreateActor("recovery-metadata", 0.50f, 1f);
        var fake = OrdinaryRecoveryTestRig.Stock(actor, 98201);
        fake.Template.Name = "Healing Potato Bread Potion";
        fake.Template.CategoryId = (int)ItemCategory.Healing_Potion;
        var heal = OrdinaryRecoveryTestRig.StockHeal(actor, 98202, 20);
        heal.Template.Name = "Unlabelled stone";
        heal.Template.CategoryId = (int)ItemCategory.Raw_Stone;

        await Assert.That(OutOfCombatRecoveryModule.IsSupportedHealthConsumable(fake)).IsFalse();
        await Assert.That(OutOfCombatRecoveryModule.SelectConsumableItem(actor.Character)).IsSameReferenceAs(heal);
    }

    [Test]
    public async Task Selection_RejectsMaxManaBonusAndNonHealingFood()
    {
        var actor = OrdinaryRecoveryTestRig.CreateActor("recovery-max-mana", 1f, 0.30f);
        var drink = OrdinaryRecoveryTestRig.StockBuff(actor, 98203,
            new BuffTemplate { Id = 98303, Duration = 30000, Kind = BuffKind.Good });
        SkillManager.Instance.GetBuffTemplate(98303).Bonuses.Add(new BonusTemplate
        {
            Attribute = UnitAttribute.MaxMana, ModifierType = UnitModifierType.Value, Value = 218
        });
        drink.Template.Name = "Vegetable Soup";
        drink.Template.CategoryId = (int)ItemCategory.Drink;
        var potato = OrdinaryRecoveryTestRig.StockBuff(actor, 98204,
            new BuffTemplate { Id = 98304, Duration = 30000, Kind = BuffKind.Bad });
        potato.Template.Name = "Cooked Potato Bread";
        potato.Template.CategoryId = (int)ItemCategory.Food;

        await Assert.That(OutOfCombatRecoveryModule.IsSupportedHealthConsumable(drink)).IsFalse();
        await Assert.That(OutOfCombatRecoveryModule.IsSupportedHealthConsumable(potato)).IsFalse();
        await Assert.That(OutOfCombatRecoveryModule.SelectConsumableItem(actor.Character)).IsNull();
        await Assert.That(OutOfCombatRecoveryModule.SelectConsumableItem(actor.Character, ConsumablePreference.Mana)).IsNull();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Selection_ProtectsActiveQuestRequirementsAndQuestTaggedItems(bool activeQuest)
    {
        var actor = OrdinaryRecoveryTestRig.CreateActor("recovery-protected");
        var item = OrdinaryRecoveryTestRig.StockHeal(actor, 98205, 20);
        if (activeQuest)
            OrdinaryRecoveryTestRig.SeedItemQuest(actor, 98405, item.TemplateId);
        else
            item.Template.LootQuestId = 98405;

        await Assert.That(OutOfCombatRecoveryModule.SelectConsumableItem(actor.Character)).IsNull();
        await Assert.That(item.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Selection_DoesNotSpendHealthSupplyForManaOnlyNeed()
    {
        var actor = OrdinaryRecoveryTestRig.CreateActor("recovery-mp-only", 1f, 0.40f);
        var item = OrdinaryRecoveryTestRig.StockHeal(actor, 98206, 20);
        await Assert.That(OutOfCombatRecoveryModule.SelectConsumableItem(actor.Character)).IsNull();
        await Assert.That(item.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Selection_RejectsUnusableLevelAndEffectLevelRange()
    {
        var actor = OrdinaryRecoveryTestRig.CreateActor("recovery-effect-level");
        var item = OrdinaryRecoveryTestRig.StockHeal(actor, 98207, 20);
        item.Template.LevelRequirement = actor.Character.Level + 1;
        await Assert.That(OutOfCombatRecoveryModule.SelectConsumableItem(actor.Character)).IsNull();
        item.Template.LevelRequirement = 0;
        SkillManager.Instance.GetSkillTemplate(item.Template.UseSkillId).Effects.Single().StartLevel = 250;
        await Assert.That(OutOfCombatRecoveryModule.SelectConsumableItem(actor.Character)).IsNull();
    }

    [Test]
    public async Task SittingPosture_UsesCharacterNativeMultiplierWithoutChangingPoints()
    {
        var actor = OrdinaryRecoveryTestRig.CreateActor("recovery-posture");
        var character = actor.Character;
        var hp = character.Hp;
        var mp = character.Mp;
        var standingHpRegen = character.HpRegen;
        var standingMpRegen = character.MpRegen;
        OutOfCombatRecoveryModule.EnterSittingStance(character);
        // Character applies the multiplier before its final integer truncation.
        await Assert.That(character.HpRegen >= standingHpRegen * 2 && character.HpRegen <= standingHpRegen * 2 + 1).IsTrue();
        await Assert.That(character.MpRegen >= standingMpRegen * 2 && character.MpRegen <= standingMpRegen * 2 + 1).IsTrue();
        await Assert.That(character.Hp).IsEqualTo(hp);
        await Assert.That(character.Mp).IsEqualTo(mp);
        OutOfCombatRecoveryModule.ExitSittingStance(character);
        await Assert.That(character.IdleStatus).IsFalse();
    }

    [Test]
    public async Task ElectionAndActivation_AreReadOnlyIncludingCompletionAndCombat()
    {
        var actor = OrdinaryRecoveryTestRig.CreateActor("recovery-pure-election", 0.80f, 1f);
        var item = OrdinaryRecoveryTestRig.StockHeal(actor, 98208, 20);
        var character = actor.Character;
        character.Stance = UnitStance.Sit; // Initial fixture posture, not generated recovery progress.
        var bot = new PlayerBotRuntime(character, "rig");
        var context = new BotActivityContext { Bot = bot, GameHour = 12f, ActiveActivity = OutOfCombatRecoveryModule.ActivityNameRest };
        var module = new OutOfCombatRecoveryModule();
        var hp = character.Hp;
        var mp = character.Mp;

        await Assert.That(module.CanActivate(context).CanActivate).IsTrue();
        module.Activate(context);
        await Assert.That(item.Count).IsEqualTo(2);
        await Assert.That(character.Hp).IsEqualTo(hp);
        await Assert.That(character.Mp).IsEqualTo(mp);
        await Assert.That(actor.AuditTrace).IsEmpty();
        character.IsInBattle = true;
        await Assert.That(module.CanActivate(context).CanActivate).IsFalse();
        await Assert.That(character.Stance).IsEqualTo(UnitStance.Sit);
        character.IsInBattle = false;
        for (var tick = 0; tick < 1000 && !OutOfCombatRecoveryModule.IsRecoveryComplete(character); tick++)
            character.OnActiveRegionTick(TimeSpan.FromSeconds(1));
        await Assert.That(OutOfCombatRecoveryModule.IsRecoveryComplete(character)).IsTrue();
        await Assert.That(module.CanActivate(context).CanActivate).IsFalse();
        await Assert.That(character.Stance).IsEqualTo(UnitStance.Sit);
        OutOfCombatRecoveryModule.ExitSittingStance(character);
    }

    [Test]
    public async Task ProducedBreadReserve_RecoveryUsesOrdinaryHealing_AndKeepsLeftoverGrain()
    {
        var actor = OrdinaryRecoveryTestRig.CreateActor("food-recovery", 0.50f, 1f);
        using var fixture = new NeedsFarmModuleTests.HardBreadFixture(actor);
        fixture.Bread.Count = 10; // Initial supplied-product recovery control, not earned-loop proof.
        GameplayActorTestRig.GrantItem(actor, NeedsFarmModuleOptions.ProcessedGrainItemId, 8);
        var hp = actor.Character.Hp;
        var result = OutOfCombatRecoveryModule.ExecuteRecoveryStep(actor.Character, actor);
        await Assert.That(result.Request?.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(actor.Character.Hp).IsGreaterThan(hp);
        await Assert.That(fixture.Bread.Count).IsEqualTo(9);
        await Assert.That(GameplayActorTestRig.BagCount(actor, NeedsFarmModuleOptions.ProcessedGrainItemId)).IsEqualTo(8);
        await Assert.That(actor.AuditTrace.Any(x => x.Action is ActorActionType.Plant or ActorActionType.Buy or ActorActionType.Craft)).IsFalse();
        OutOfCombatRecoveryModule.ExitSittingStance(actor.Character);
    }
}

/// <summary>
/// Fixture initial state only: managers, ordinary fixed heal/timed-buff rows,
/// bag stock, deficits and an optional active quest. Actions/effects/progress
/// are never credited by assigning post-action points or quest completion.
/// </summary>
internal static class OrdinaryRecoveryTestRig
{
    internal static GameplayActor CreateActor(string name, float hp = 0.50f, float mp = 0.50f)
    {
        var (actor, _) = GameplayActorTestRig.CreateActor($"{name}-{Guid.NewGuid():N}");
        actor.Character.Hp = (int)(actor.Character.MaxHp * hp);
        actor.Character.Mp = (int)(actor.Character.MaxMp * mp);
        actor.Character.Breath = 1;
        actor.Character.IsInBattle = false;
        actor.Character.Stance = UnitStance.Stand;
        return actor;
    }

    internal static Item Stock(GameplayActor actor, uint itemId, uint skillId = 0)
    {
        GameplayActorTestRig.SeedItemTemplate(itemId, skillId);
        var template = ItemManager.Instance.GetTemplate(itemId);
        template.LootQuestId = 0;
        template.LevelRequirement = 0;
        template.CategoryId = 0;
        GameplayActorTestRig.GrantItem(actor, itemId, 2);
        return actor.Character.Inventory.Bag.GetItemsSnapshot().Single(i => i.TemplateId == itemId);
    }

    internal static Item StockHeal(GameplayActor actor, uint itemId, int amount, int castTime = 0)
    {
        var item = Stock(actor, itemId, itemId + 1000);
        var skill = SeedSkill(item.Template.UseSkillId, castTime);
        skill.Effects = [Effect(new HealEffect { Id = itemId, UseFixedHeal = true, FixedMin = amount, FixedMax = amount })];
        return item;
    }

    internal static Item StockBuff(GameplayActor actor, uint itemId, BuffTemplate buff, int castTime = 0)
    {
        var item = Stock(actor, itemId, itemId + 1000);
        var skill = SeedSkill(item.Template.UseSkillId, castTime);
        Dictionary<uint, BuffTemplate>(SkillManager.Instance, "_buffs")[buff.Id] = buff;
        skill.Effects = [Effect(new BuffEffect { Id = itemId, Chance = 100, Buff = buff })];
        return item;
    }

    internal static Item StockTimedHeal(GameplayActor actor, uint itemId, uint buffId, int castTime = 0)
    {
        var healId = itemId + 2000;
        var types = Dictionary<uint, EffectType>(SkillManager.Instance, "_types");
        types[healId] = new EffectType { Id = healId, ActualId = healId, Type = nameof(HealEffect) };
        var effects = Dictionary<string, Dictionary<uint, EffectTemplate>>(SkillManager.Instance, "_effects");
        if (!effects.TryGetValue(nameof(HealEffect), out var heals))
            effects[nameof(HealEffect)] = heals = [];
        heals[healId] = new HealEffect { Id = healId, UseFixedHeal = true, FixedMin = 600, FixedMax = 600 };
        var buff = new BuffTemplate { Id = buffId, Duration = 30000, Tick = 2000, Kind = BuffKind.Good };
        buff.TickEffects.Add(new TickEffect { EffectId = healId });
        return StockBuff(actor, itemId, buff, castTime);
    }

    private static SkillTemplate SeedSkill(uint skillId, int castTime)
    {
        GameplayActorTestRig.SeedSkillTemplate(skillId);
        var skill = SkillManager.Instance.GetSkillTemplate(skillId);
        skill.CastingTime = castTime;
        skill.EffectDelay = 0;
        skill.CooldownTime = 0;
        skill.IgnoreGlobalCooldown = true;
        return skill;
    }

    private static SkillEffect Effect(EffectTemplate template) => new()
    {
        Template = template, EffectId = template.Id, Chance = 100, Friendly = true, NonFriendly = true,
        Front = true, Back = true, StartLevel = 0, EndLevel = byte.MaxValue,
        ApplicationMethod = SkillEffectApplicationMethod.Target, ConsumeSourceItem = true, ConsumeItemCount = 1
    };

    internal static Quest SeedItemQuest(GameplayActor actor, uint questId, uint itemId)
    {
        var template = new QuestTemplate { Id = questId, Level = 1 };
        var component = new QuestComponentTemplate(template) { Id = questId + 1000, KindId = QuestComponentKind.Progress };
        component.ActTemplates.Add(new QuestActObjItemGather(component) { ActId = questId + 2000, ItemId = itemId, Count = 2 });
        template.Components[component.Id] = component;
        Dictionary<uint, QuestTemplate>(QuestManager.Instance, "_questTemplates")[questId] = template;
        var quest = new Quest(actor.Character) { TemplateId = questId, Status = QuestStatus.Progress,
            Step = QuestComponentKind.Progress, Objectives = [0, 0, 0, 0, 0] };
        actor.Character.Quests.ActiveQuests[questId] = quest;
        return quest;
    }

    private static Dictionary<TKey, TValue> Dictionary<TKey, TValue>(object manager, string fieldName) where TKey : notnull
    {
        var field = manager.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance)!;
        if (field.GetValue(manager) is Dictionary<TKey, TValue> dictionary)
            return dictionary;
        var created = new Dictionary<TKey, TValue>();
        field.SetValue(manager, created);
        return created;
    }
}
