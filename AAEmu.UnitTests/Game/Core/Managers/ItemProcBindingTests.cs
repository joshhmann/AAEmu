using System.Collections.Concurrent;
using System.Reflection;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Game.Items.Containers;
using AAEmu.Game.Models.Game.Items.Procs;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Units;
using Microsoft.Extensions.DependencyInjection;

namespace AAEmu.UnitTests.Game.Core.Managers;

/// <summary>
/// C8 / ITEM-01 slice — item_proc_bindings: items can carry procs.
///
/// compact.sqlite3 schema (read-only reference):
///   item_procs(id, name, description, skill_id, chance_kind_id, chance_rate,
///              chance_param, cooldown_sec, finisher, item_level_based_chance_bonus)
///   item_proc_bindings(id, item_id, proc_id) — proc_id → item_procs.id
///
/// Loader: ItemManager.Load reads bindings right after item_procs and groups
/// proc ids per item template. Wiring: Unit.ApplyEquipItemProcs syncs the
/// bound procs of currently equipped items into UnitProcs on every gear
/// change; the existing trigger path (DamageEffect → RollProcsForKind) fires
/// them.
/// </summary>
[NotInParallel]
public class ItemProcBindingTests
{
    // Rig-local id ranges — never collide with other suites' fixtures.
    private const uint ProcTemplateId = 9100;
    private const uint BoundItemId = 9101;   // equippable template WITH a binding
    private const uint UnboundItemId = 9102; // equippable template WITHOUT a binding
    private const uint RigOwnerIdBase = 920000;

    #region Manager accessor

    [Test]
    public async Task GetItemProcBindings_BoundItemId_ReturnsBoundProcIds()
    {
        var manager = CreateItemManager();
        SetPrivateField(manager, "_itemProcBindings", new Dictionary<uint, List<uint>>
        {
            { BoundItemId, [ProcTemplateId] }
        });

        var result = manager.GetItemProcBindings(BoundItemId);

        await Assert.That(result).IsNotEmpty();
        await Assert.That(result.Single()).IsEqualTo(ProcTemplateId);
    }

    [Test]
    public async Task GetItemProcBindings_UnboundItemId_ReturnsEmptyList()
    {
        var manager = CreateItemManager();
        SetPrivateField(manager, "_itemProcBindings", new Dictionary<uint, List<uint>>
        {
            { BoundItemId, [ProcTemplateId] }
        });

        var result = manager.GetItemProcBindings(UnboundItemId);

        await Assert.That(result).IsEmpty();
    }

    [Test]
    public async Task GetItemProcBindings_MultipleBindingsSameItem_ReturnsAllProcIds()
    {
        var manager = CreateItemManager();
        SetPrivateField(manager, "_itemProcBindings", new Dictionary<uint, List<uint>>
        {
            { BoundItemId, [ProcTemplateId, ProcTemplateId + 1] }
        });

        var result = manager.GetItemProcBindings(BoundItemId);

        await Assert.That(result.Count).IsEqualTo(2);
    }

    #endregion

    #region Trigger seam — UnitProcs with a fake effect sink

    [Test]
    public async Task RollProcsForKind_OffCooldown_FiresBoundProcEffect()
    {
        SeedSingletons();

        var fired = new List<uint>();
        var procs = new UnitProcs(new RigCharacter(), id => new SpyProc(id, fired));
        procs.AddProc(ProcTemplateId); // ChanceKind = HitAny, ChanceRate = 100

        procs.RollProcsForKind(ProcChanceKind.HitAny);

        await Assert.That(fired.Count).IsEqualTo(1);
        await Assert.That(fired[0]).IsEqualTo(ProcTemplateId);
    }

    [Test]
    public async Task RollProcsForKind_OnCooldown_DoesNotFireAgain()
    {
        SeedSingletons();

        var fired = new List<uint>();
        var procs = new UnitProcs(new RigCharacter(), id => new SpyProc(id, fired));
        procs.AddProc(ProcTemplateId); // CooldownSec = 60 on the fixture template

        procs.RollProcsForKind(ProcChanceKind.HitAny);
        procs.RollProcsForKind(ProcChanceKind.HitAny); // still on cooldown

        await Assert.That(fired.Count).IsEqualTo(1);
    }

    [Test]
    public async Task RollProcsForKind_UnrelatedChanceKind_DoesNotFire()
    {
        SeedSingletons();

        var fired = new List<uint>();
        var procs = new UnitProcs(new RigCharacter(), id => new SpyProc(id, fired));
        procs.AddProc(ProcTemplateId); // HitAny only

        procs.RollProcsForKind(ProcChanceKind.TakeDamageAny);

        await Assert.That(fired.Count).IsEqualTo(0);
    }

    #endregion

    #region Equip rig — bindings reach the character through the real gear-change path

    [Test]
    public async Task Equip_BoundItem_AttachesBoundProcToCharacter()
    {
        SeedSingletons();
        using var rig = new EquipRig();

        var item = EquipItem(rig.Character, BoundItemId, slot: 2);
        rig.Character.Procs.RollProcsForKind(ProcChanceKind.HitAny);

        await Assert.That(item).IsNotNull();
        await Assert.That(rig.FiredProcIds.Count).IsEqualTo(1);
        await Assert.That(rig.FiredProcIds[0]).IsEqualTo(ProcTemplateId);
    }

    [Test]
    public async Task Unequip_BoundItem_DetachesBoundProcFromCharacter()
    {
        SeedSingletons();
        using var rig = new EquipRig();

        var item = EquipItem(rig.Character, BoundItemId, slot: 2);
        rig.Character.Inventory.Equipment.RemoveItem(ItemTaskType.Invalid, item, false);
        rig.FiredProcIds.Clear();
        rig.Character.Procs.RollProcsForKind(ProcChanceKind.HitAny);

        await Assert.That(rig.FiredProcIds.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Equip_UnboundItem_FiresNoProcEffect()
    {
        SeedSingletons();
        using var rig = new EquipRig();

        var item = EquipItem(rig.Character, UnboundItemId, slot: 2);
        rig.Character.Procs.RollProcsForKind(ProcChanceKind.HitAny);

        await Assert.That(item).IsNotNull();
        await Assert.That(rig.FiredProcIds.Count).IsEqualTo(0);
    }

    #endregion

    #region Helpers

    private static Item EquipItem(Character character, uint templateId, int slot)
    {
        var item = ItemManager.Instance.Create(templateId, 1, 0);
        if (item == null)
            return null;

        item.SlotType = SlotType.Equipment;
        item.Slot = (byte)slot;
        character.Inventory.Equipment.AddOrMoveExistingItem(ItemTaskType.Invalid, item, slot);
        return item;
    }

    /// <summary>
    /// Fake effect sink: records Apply calls instead of casting the bound
    /// skill (Skill.Use needs a full world simulation).
    /// </summary>
    private sealed class SpyProc(uint templateId, List<uint> sink) : ItemProc(templateId)
    {
        public override bool Apply(Unit owner, bool ignoreRoll = false)
        {
            sink.Add(TemplateId);
            return true;
        }
    }

    /// <summary>Rig character whose UnitProcs uses the fake effect sink.</summary>
    private sealed class RigCharacter : Character
    {
        public RigCharacter() : base(null)
        {
            Procs = new UnitProcs(this);
        }

        public override void BroadcastPacket(GamePacket packet, bool self)
        {
        }
    }

    /// <summary>Headless equip rig: character + fired-proc sink, disposable.</summary>
    private sealed class EquipRig : IDisposable
    {
        public Character Character { get; } = BuildCharacter();
        public List<uint> FiredProcIds { get; } = [];

        public EquipRig()
        {
            // Route the rig character's UnitProcs through the fake effect sink.
            var procs = new UnitProcs(Character, id => new SpyProc(id, FiredProcIds));
            typeof(Unit).GetProperty(nameof(Unit.Procs))!
                .GetSetMethod(true)!
                .Invoke(Character, [procs]);
        }

        public void Dispose()
        {
            CleanupFixtureContainers();
        }
    }

    private static RigCharacter BuildCharacter()
    {
        var character = new RigCharacter
        {
            Id = RigOwnerIdBase + 100,
            Name = "ProcRig01",
            Level = 10,
            Race = Race.Nuian,
            Gender = Gender.Male
        };
        character.Inventory = new Inventory(character);
        character.Skills = new CharacterSkills(character);
        character.Appellations = new CharacterAppellations(character);
        character.Abilities = new CharacterAbilities(character);
        character.VisualOptions = new CharacterVisualOptions();
        return character;
    }

    private static void SeedSingletons()
    {
        if (typeof(Singleton<ItemManager>).GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)
                ?.GetValue(null) is null)
        {
            typeof(Singleton<ItemManager>).GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)!
                .SetValue(null, BuildFixtureItemManager());
        }
        ContainerIdManager.Instance.Initialize(false);

        if (!SingletonSeeded(typeof(Singleton<SkillManager>)))
        {
            var skillManager = new SkillManager(Mock.Of<IAnimationManager>().Object, Mock.Of<IPlotManager>().Object);
            typeof(Singleton<SkillManager>).GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)!
                .SetValue(null, skillManager);
        }

        var manager = SkillManager.Instance;
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        foreach (var field in typeof(SkillManager).GetFields(flags).Where(f => f.FieldType.IsGenericType
                     && f.FieldType.GetGenericTypeDefinition() == typeof(Dictionary<,>)))
        {
            if (field.GetValue(manager) == null)
            {
                var dictType = typeof(Dictionary<,>).MakeGenericType(
                    field.FieldType.GetGenericArguments()[0], field.FieldType.GetGenericArguments()[1]);
                field.SetValue(manager, Activator.CreateInstance(dictType));
            }
        }

        var buffs = (Dictionary<uint, BuffTemplate>)typeof(SkillManager).GetField("_buffs", flags)!
            .GetValue(manager)!;
        foreach (var id in new[] { 8000011u, 8000012u })
        {
            if (!buffs.ContainsKey(id))
                buffs[id] = new BuffTemplate { Id = id, Duration = 1, Kind = BuffKind.Good };
        }

        var buffGameData = BuffGameData.Instance;
        foreach (var field in typeof(BuffGameData).GetFields(flags).Where(f => f.FieldType.IsGenericType
                     && f.FieldType.GetGenericTypeDefinition() == typeof(Dictionary<,>)))
        {
            if (field.GetValue(buffGameData) == null)
            {
                var dictType = typeof(Dictionary<,>).MakeGenericType(
                    field.FieldType.GetGenericArguments()[0], field.FieldType.GetGenericArguments()[1]);
                field.SetValue(buffGameData, Activator.CreateInstance(dictType));
            }
        }

        var itemGameData = ItemGameData.Instance;
        if (typeof(ItemGameData).GetField("_itemGradeBuffs", flags)!.GetValue(itemGameData) == null)
        {
            typeof(ItemGameData).GetField("_itemGradeBuffs", flags)!
                .SetValue(itemGameData, new Dictionary<uint, Dictionary<byte, uint>>());
        }

        if (!SingletonSeeded(typeof(Singleton<EffectTaskManager>)))
        {
            typeof(Singleton<EffectTaskManager>).GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)!
                .SetValue(null, new EffectTaskManager(Mock.Of<ITaskManager>().Object));
        }

        if (!SingletonSeeded(typeof(Singleton<QuestManager>)))
        {
            var questManager = new QuestManager(Mock.Of<ITaskManager>().Object, Mock.Of<IZoneManager>().Object);
            SetPrivateField(questManager, "_componentTemplates", new Dictionary<uint, QuestComponentTemplate>());
            SetPrivateField(questManager, "_groupItems", new Dictionary<uint, List<uint>>());
            SetPrivateField(questManager, "_groupNpcs", new Dictionary<uint, List<uint>>());
            typeof(Singleton<QuestManager>).GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)!
                .SetValue(null, questManager);
        }

        // This suite's own data: proc templates + bindings on whichever
        // ItemManager instance is live (missing-only seeding above may have
        // adopted one from another rig class).
        var itemManager = ItemManager.Instance;
        SetPrivateField(itemManager, "_templates", new Dictionary<uint, ItemTemplate>
        {
            // BodyPartTemplate slot_type_id 3 → EquipmentItemSlot.Chest (slot 2)
            { BoundItemId, new BodyPartTemplate { Id = BoundItemId, SlotTypeId = 3 } },
            { UnboundItemId, new BodyPartTemplate { Id = UnboundItemId, SlotTypeId = 3 } },
        });
        SetPrivateField(itemManager, "_itemProcTemplates", new Dictionary<uint, ItemProcTemplate>
        {
            {
                ProcTemplateId,
                new ItemProcTemplate
                {
                    Id = ProcTemplateId,
                    SkillId = 1,
                    ChanceKind = ProcChanceKind.HitAny,
                    ChanceRate = 100,
                    CooldownSec = 60
                }
            },
        });
        SetPrivateField(itemManager, "_itemProcBindings", new Dictionary<uint, List<uint>>
        {
            { BoundItemId, [ProcTemplateId] }, // unbound item deliberately absent
        });
        SetPrivateField(itemManager, "_itemUnitModifiers", new Dictionary<uint, List<BonusTemplate>>());
        SetPrivateField(itemManager, "_equipItemSets", new Dictionary<uint, EquipItemSet>());
    }

    private static bool SingletonSeeded(Type singletonBase)
        => singletonBase.GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)
            ?.GetValue(null) != null;

    private static ItemManager BuildFixtureItemManager()
    {
        var itemManager = new ItemManager(
            Mock.Of<ISkillManager>().Object,
            new CountingItemIdManager(),
            Mock.Of<IContainerIdManager>().Object,
            Mock.Of<ILocalizationManager>().Object,
            Mock.Of<ITaskManager>().Object,
            Mock.Of<IWorldManager>().Object);

        SetPrivateField(itemManager, "_allPersistentContainers", new ConcurrentDictionary<ulong, ItemContainer>());
        SetPrivateField(itemManager, "_allItems", new ConcurrentDictionary<ulong, Item>());
        SetPrivateField(itemManager, "_removedItems", new List<ulong>());

        return itemManager;
    }

    private static void CleanupFixtureContainers()
    {
        var itemManager = ItemManager.Instance;
        if (typeof(ItemManager).GetField("_allPersistentContainers", BindingFlags.NonPublic | BindingFlags.Instance)
                ?.GetValue(itemManager) is not System.Collections.IDictionary containers)
            return;
        if (typeof(ItemManager).GetField("_allItems", BindingFlags.NonPublic | BindingFlags.Instance)
                ?.GetValue(itemManager) is not System.Collections.IDictionary allItems)
            return;

        var toRemove = new List<object>();
        foreach (System.Collections.DictionaryEntry entry in containers)
        {
            if (entry.Value is not ItemContainer container || container.OwnerId < RigOwnerIdBase)
                continue;
            toRemove.Add(entry.Key);
            foreach (var item in container.Items.ToList())
                allItems.Remove(item.Id);
        }
        foreach (var key in toRemove)
            containers.Remove(key);
    }

    private static ItemManager CreateItemManager()
    {
        return new ItemManager(
            Mock.Of<ISkillManager>().Object,
            Mock.Of<IItemIdManager>().Object,
            Mock.Of<IContainerIdManager>().Object,
            Mock.Of<ILocalizationManager>().Object,
            Mock.Of<ITaskManager>().Object,
            Mock.Of<IWorldManager>().Object);
    }

    private static void SetPrivateField(object obj, string fieldName, object value)
    {
        var field = obj.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
        if (field == null)
            throw new InvalidOperationException($"Field {fieldName} not found on {obj.GetType().Name}");
        field.SetValue(obj, value);
    }

    /// <summary>Hand-rolled IItemIdManager — increments from 1.</summary>
    private sealed class CountingItemIdManager : IItemIdManager
    {
        private uint _next = 1;
        public bool Initialize(bool forceReset = false) => true;
        public uint GetNextId() => _next++;
        public uint[] GetNextId(int count)
        {
            var result = new uint[count];
            for (var i = 0; i < count; i++)
                result[i] = GetNextId();
            return result;
        }
        public void ReleaseId(uint usedObjectId) { }
        public void ReleaseId(IEnumerable<uint> usedObjectIds) { }
        public void Load() { }
    }

    #endregion
}
