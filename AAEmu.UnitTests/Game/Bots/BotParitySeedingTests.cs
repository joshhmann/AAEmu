using System.Collections.Concurrent;
using System.Reflection;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Char.Templates;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Containers;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.StaticValues;

namespace AAEmu.UnitTests.Game.Bots;

/// <summary>
/// M6.6 player-parity seeding rig (t_747a1c44): the shared create-path
/// progression seeding (CharacterManager.ApplyPlayerProgression) and the
/// starter-bag supply heal (CharacterManager.ApplyStarterBagSupplies) must
/// mirror the human create path exactly. Fail-before: an unseeded provisioned
/// character has 0 skill rows, 0 actability entries, an all-None action bar
/// (85B blob) and an empty bag. Pass-after: seeded skills, the full actability
/// set, spell slots + item slots in the create-path order, and bag supplies —
/// idempotent on re-run (adopt-path heal).
///
/// Hermetic: mocked ISkillManager + in-memory CharacterManager with a seeded
/// actability catalog; NO MySQL. The real 34-row / starter-kit counts ride the
/// live rig + prod verification.
/// </summary>
[NotInParallel]
public class BotParitySeedingTests
{
    private const byte MaxActionSlots = 85; // Character.MaxActionSlots

    // ------------------------------------------------------------------ helpers

    private static Character BuildUnseededCharacter(CharacterManager manager, bool withInventory = false)
    {
        var character = new Character(new UnitCustomModelParams())
        {
            Id = 424242,
            Name = "SeedingBot",
            Level = 1,
            Race = Race.Nuian,
            Gender = Gender.Male,
            Ability1 = AbilityType.Fight,
            Ability2 = AbilityType.Magic,
            Ability3 = AbilityType.Will,
            NumInventorySlots = 50,
            NumBankSlots = 50
        };

        character.Slots = new ActionSlot[MaxActionSlots];
        for (var i = 0; i < character.Slots.Length; i++)
            character.Slots[i] = new ActionSlot();

        // Mirror HeadlessSession.BuildProvisionedCharacter's fail-before
        // state: Abilities created (10-row dict) but Skills/Actability are
        // never constructed and no action slot is set.
        character.Abilities = new CharacterAbilities(character);
        if (withInventory)
            character.Inventory = new Inventory(character);
        return character;
    }

    private static CharacterManager BuildCharacterManager(
        out List<SkillTemplate> startAbilitySkills,
        out List<DefaultSkill> defaultSkills)
    {
        startAbilitySkills =
        [
            // Start-ability skills: LevelStep 0 so AddSkill's level math takes
            // the (byte)1 branch and never touches ExperienceManager.
            new SkillTemplate { Id = 1000, AbilityId = AbilityType.Fight, LevelStep = 0 },
            new SkillTemplate { Id = 1001, AbilityId = AbilityType.Fight, LevelStep = 0 }
        ];

        // Mirrors the canonical default_skills shape: fixed slot_index
        // entries (AddToSlot=true) — incl. a slot-13 entry that collides with
        // the 4th starter-supply item slot, exactly like the real 1.2 data
        // (skill 16287 at slot 13). The human create path's order (supplies
        // 10..13 first, then default skills) makes the spell OVERWRITE the
        // item — the blob math must match that.
        defaultSkills =
        [
            new DefaultSkill { Template = new SkillTemplate { Id = 2, LevelStep = 0 }, Slot = 1, AddToSlot = true },
            new DefaultSkill { Template = new SkillTemplate { Id = 16064, LevelStep = 0 }, Slot = 2, AddToSlot = true },
            new DefaultSkill { Template = new SkillTemplate { Id = 16287, LevelStep = 0 }, Slot = 13, AddToSlot = true },
            new DefaultSkill { Template = new SkillTemplate { Id = 14495, LevelStep = 0 }, Slot = 14, AddToSlot = true },
            new DefaultSkill { Template = new SkillTemplate { Id = 14152, LevelStep = 0 }, Slot = 20, AddToSlot = true },
            new DefaultSkill { Template = new SkillTemplate { Id = 10594, LevelStep = 0 }, Slot = 21, AddToSlot = true },
            new DefaultSkill { Template = new SkillTemplate { Id = 14900, LevelStep = 0 }, Slot = 22, AddToSlot = true },
            new DefaultSkill { Template = new SkillTemplate { Id = 14902, LevelStep = 0 }, Slot = 23, AddToSlot = true },
            new DefaultSkill { Template = new SkillTemplate { Id = 14901, LevelStep = 0 }, Slot = 24, AddToSlot = true },
            new DefaultSkill { Template = new SkillTemplate { Id = 14451, LevelStep = 0 }, Slot = 50, AddToSlot = false }
        ];

        var skillManagerMock = Mock.Of<ISkillManager>();
        skillManagerMock.GetStartAbilitySkills(AbilityType.Fight).Returns(startAbilitySkills);
        skillManagerMock.GetDefaultSkills().Returns(defaultSkills);
        var skillManager = skillManagerMock.Object;

        var manager = new CharacterManager(
            Mock.Of<IWorldManager>().Object,
            Mock.Of<IAccountManager>().Object,
            Mock.Of<INameManager>().Object,
            Mock.Of<ICharacterIdManager>().Object,
            Mock.Of<IFactionManager>().Object,
            skillManager,
            Mock.Of<IItemManager>().Object,
            Mock.Of<IHousingManager>().Object,
            Mock.Of<IFamilyManager>().Object,
            Mock.Of<IMailManager>().Object,
            Mock.Of<ITaskManager>().Object);

        // Seed the actability catalog (the 1.2 data has 34 groups; two are
        // enough to prove the full-set seeding mechanism here).
        SetPrivateField(manager, "_actabilities", new Dictionary<uint, ActabilityTemplate>
        {
            [1] = new() { Id = 1, Name = "Farming" },
            [2] = new() { Id = 2, Name = "Mining" }
        });

        return manager;
    }

    private static void SetPrivateField(object instance, string fieldName, object value)
    {
        var field = instance.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Cannot locate field {fieldName}");
        field.SetValue(instance, value);
    }

    private static byte[] GetActionSlotsAsBlob(Character character)
    {
        var method = typeof(Character).GetMethod("GetActionSlotsAsBlob", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("GetActionSlotsAsBlob not found");
        return (byte[])method.Invoke(character, null);
    }

    private static int CountPopulatedSlots(Character character)
        => character.Slots.Count(s => s.Type != ActionSlotType.None);

    // ------------------------------------------------------------------ fail-before

    [Test]
    public async Task FailBefore_UnseededProvisionedCharacter_HasNoPlayerData()
    {
        var manager = BuildCharacterManager(out _, out _);
        var character = BuildUnseededCharacter(manager);

        // The parity-audit signature: bots had 0 skill rows, 0 actability
        // rows, an all-None action bar (85B blob) and an empty bag.
        await Assert.That(character.Skills).IsNull();
        await Assert.That(character.Actability).IsNull();
        await Assert.That(CountPopulatedSlots(character)).IsZero();
        await Assert.That(GetActionSlotsAsBlob(character)).HasCount(MaxActionSlots); // 1B per None slot
    }

    // ------------------------------------------------------------------ pass-after: progression

    [Test]
    public async Task PassAfter_ApplyPlayerProgression_SeedsHumanCreateShape()
    {
        var manager = BuildCharacterManager(out var startAbilitySkills, out _);
        var character = BuildUnseededCharacter(manager);

        manager.ApplyPlayerProgression(character);

        // Skills: exactly the start-ability set, level 1 (like Asssaa's row).
        await Assert.That(character.Skills).IsNotNull();
        await Assert.That(character.Skills.Skills.Count).IsEqualTo(startAbilitySkills.Count);
        await Assert.That(character.Skills.Skills[1000u].Level).IsEqualTo((byte)1);
        await Assert.That(character.Skills.Skills.ContainsKey(1001u)).IsTrue();

        // Actability: the full catalog (seed-if-missing).
        await Assert.That(character.Actability).IsNotNull();
        await Assert.That(character.Actability.Actabilities.Count).IsEqualTo(2);

        // Action bar: default skills at their fixed slots + start-ability
        // skills in the first free slots (1,2 occupied → 3,4).
        await Assert.That(character.Slots[1].Type).IsEqualTo(ActionSlotType.Spell);
        await Assert.That(character.Slots[1].ActionId).IsEqualTo(2u);
        await Assert.That(character.Slots[3].Type).IsEqualTo(ActionSlotType.Spell);
        await Assert.That(character.Slots[3].ActionId).IsEqualTo(1000u);
        await Assert.That(character.Slots[4].ActionId).IsEqualTo(1001u);
        await Assert.That(character.Slots[50].Type).IsEqualTo(ActionSlotType.None); // AddToSlot=false → no slot
    }

    [Test]
    public async Task PassAfter_ActionBarBlob_MirrorsCreatePathCollisionMath()
    {
        // The full create-path order: starter supplies occupy item slots
        // 10..13 first, then ApplyPlayerProgression lays down default-skill
        // spell slots (1,2,13,14,20..24) — slot 13 OVERWRITES the 4th supply —
        // then start-ability skills in the first free slots (3,4). The blob
        // length must match the human shape (Asssaa: 13 populated → 137B).
        var manager = BuildCharacterManager(out _, out _);
        var character = BuildUnseededCharacter(manager);

        // Supplies first (what ApplyStartingEquipment does for fresh bots).
        for (var i = 0; i < 4; i++)
            character.SetAction((byte)(10 + i), ActionSlotType.ItemType, (uint)(5000 + i));

        manager.ApplyPlayerProgression(character);

        var expectedPopulated = new HashSet<byte> { 1, 2, 3, 4, 10, 11, 12, 13, 14, 20, 21, 22, 23, 24 };
        await Assert.That(CountPopulatedSlots(character)).IsEqualTo(expectedPopulated.Count);
        foreach (var slot in expectedPopulated)
            await Assert.That(character.Slots[slot].Type).IsNotEqualTo(ActionSlotType.None);

        // Slot 13 is the spell (default skill 16287), not the 4th supply item.
        await Assert.That(character.Slots[13].Type).IsEqualTo(ActionSlotType.Spell);
        await Assert.That(character.Slots[13].ActionId).IsEqualTo(16287u);

        // Blob = 85 × 1B (None) + populated × 4B.
        await Assert.That(GetActionSlotsAsBlob(character)).HasCount(MaxActionSlots + expectedPopulated.Count * 4);
    }

    [Test]
    public async Task Progression_IsIdempotent_NoDoubleSeed()
    {
        var manager = BuildCharacterManager(out var startAbilitySkills, out _);
        var character = BuildUnseededCharacter(manager);

        manager.ApplyPlayerProgression(character);
        var blobAfterFirst = GetActionSlotsAsBlob(character);

        manager.ApplyPlayerProgression(character);

        await Assert.That(character.Skills.Skills.Count).IsEqualTo(startAbilitySkills.Count);
        await Assert.That(character.Actability.Actabilities.Count).IsEqualTo(2);
        await Assert.That(GetActionSlotsAsBlob(character)).IsEquivalentTo(blobAfterFirst);
    }

    // ------------------------------------------------------------------ pass-after: bag supplies

    [Test]
    public async Task PassAfter_ApplyStarterBagSupplies_FillsBagAndItemSlots_OnlyWhenEmpty()
    {
        // Install the supply-capable ItemManager fixture for this test only,
        // restoring whatever was there before (t_4f11a519 singleton
        // discipline — never permanently replace an established manager).
        var previousItemManager = GetSingleton(typeof(Singleton<ItemManager>));
        SeedItemManagerWithSupplyTemplates();
        // Item acquisition routes through QuestManager.DoItemsAcquiredEvents
        // (Inventory.cs:924) — install a no-op quest manager so the
        // singleton lazy-init doesn't throw (no parameterless ctor).
        var previousQuestManager = GetSingleton(typeof(Singleton<QuestManager>));
        SetSingleton(typeof(Singleton<QuestManager>),
            new QuestManager(Mock.Of<ITaskManager>().Object, Mock.Of<IZoneManager>().Object));
        try
        {
            var manager = BuildCharacterManager(out _, out _);
            var character = BuildUnseededCharacter(manager, withInventory: true);

            // Fail-before: the bag is empty (the parity-audit signature).
            await Assert.That(character.Inventory.Bag.Items).IsEmpty();

            // Seed the ability-0 supply kit into the manager (the canonical
            // data has only ability-0 supplies: 4045×1, 18791×3, 18792×3,
            // 417×3 — GetStartingAbilityEquipment merges ability-0 into any
            // ability plan).
            SetPrivateField(manager, "_abilityItems", new Dictionary<byte, AbilityItems>
            {
                [0] = new()
                {
                    Ability = 0,
                    Items = new EquipItemsTemplate(),
                    Supplies =
                    [
                        new AbilitySupplyItem { Id = 4045, Amount = 1, Grade = 0 },
                        new AbilitySupplyItem { Id = 18791, Amount = 3, Grade = 0 },
                        new AbilitySupplyItem { Id = 18792, Amount = 3, Grade = 0 },
                        new AbilitySupplyItem { Id = 417, Amount = 3, Grade = 0 }
                    ]
                }
            });

            manager.ApplyStarterBagSupplies(character);

            // The starter kit lands in the bag (4 distinct stacks) and the
            // item action slots 10..13 (like the human row).
            await Assert.That(character.Inventory.Bag.Items.Count).IsEqualTo(4);
            await Assert.That(character.Inventory.Bag.Items.Select(i => i.TemplateId))
                .IsEquivalentTo(new[] { 4045u, 18791u, 18792u, 417u });
            await Assert.That(character.Slots[10].Type).IsEqualTo(ActionSlotType.ItemType);
            await Assert.That(character.Slots[10].ActionId).IsEqualTo(4045u);
            await Assert.That(character.Slots[13].Type).IsEqualTo(ActionSlotType.ItemType);
            await Assert.That(character.Slots[13].ActionId).IsEqualTo(417u);

            // Idempotent heal: a bag that already holds items is left alone.
            var countAfter = character.Inventory.Bag.Items.Count;
            manager.ApplyStarterBagSupplies(character);
            await Assert.That(character.Inventory.Bag.Items.Count).IsEqualTo(countAfter);
        }
        finally
        {
            SetSingleton(typeof(Singleton<ItemManager>), previousItemManager);
            SetSingleton(typeof(Singleton<QuestManager>), previousQuestManager);
        }
    }

    // ------------------------------------------------------------------ singleton seeding

    /// <summary>
    /// Installs a fixture ItemManager that can serve the starter-kit
    /// templates (GetTemplate/Create/GetItemContainerForCharacter). The
    /// caller captures the previous singleton and restores it after the
    /// test (t_4f11a519 discipline).
    /// </summary>
    private static void SeedItemManagerWithSupplyTemplates()
    {
        var itemIdManagerMock = Mock.Of<IItemIdManager>();
        var nextItemId = 1u;
        itemIdManagerMock.GetNextId().Returns(() => nextItemId++);
        var itemIdManager = itemIdManagerMock.Object;

        var itemManager = new ItemManager(
            Mock.Of<ISkillManager>().Object,
            itemIdManager,
            Mock.Of<IContainerIdManager>().Object,
            Mock.Of<ILocalizationManager>().Object,
            Mock.Of<ITaskManager>().Object,
            Mock.Of<IWorldManager>().Object);

        SetPrivateField(itemManager, "_templates", new Dictionary<uint, ItemTemplate>
        {
            [4045] = BuildSupplyTemplate(4045),
            [18791] = BuildSupplyTemplate(18791),
            [18792] = BuildSupplyTemplate(18792),
            [417] = BuildSupplyTemplate(417)
        });
        SetPrivateField(itemManager, "_allPersistentContainers", new ConcurrentDictionary<ulong, ItemContainer>());
        SetPrivateField(itemManager, "_allItems", new ConcurrentDictionary<ulong, Item>());
        SetPrivateField(itemManager, "_removedItems", new List<ulong>());

        SetSingleton(typeof(Singleton<ItemManager>), itemManager);
        ContainerIdManager.Instance.Initialize(false); // no-op when established
    }

    private static ItemTemplate BuildSupplyTemplate(uint id) => new()
    {
        Id = id,
        Name = "Supply " + id,
        MaxCount = 100,
        FixedGrade = 0,
        Gradable = false
    };

    private static object GetSingleton(Type singletonBase)
    {
        var field = singletonBase.GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException($"Cannot locate singleton field on {singletonBase.Name}");
        return field.GetValue(null);
    }

    private static void SetSingleton(Type singletonBase, object instance)
    {
        var field = singletonBase.GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException($"Cannot locate singleton field on {singletonBase.Name}");
        field.SetValue(null, instance);
    }
}
