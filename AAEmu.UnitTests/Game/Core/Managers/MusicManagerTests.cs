using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using System.Collections.Concurrent;
using System.Reflection;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Game.Items.Containers;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.Music;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Effects.SpecialEffects;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.UnitTests.Game.Core.Managers;

public class MusicManagerTests
{
    [Test]
    public async Task Constructor_DoesNotCallDeps()
    {
        var mockMusicId = Mock.Of<IMusicIdManager>();
        var mockItem = Mock.Of<IItemManager>();
        var manager = new MusicManager(mockMusicId.Object, mockItem.Object);

        await Assert.That(manager).IsNotNull();
        Mock.VerifyNoOtherCalls(mockMusicId);
        Mock.VerifyNoOtherCalls(mockItem);
    }
}

/// <summary>
/// MUSIC-01 slice — instrument_sounds mapping: every instrument item gets its
/// correct play buff from reference data.
///
/// compact.sqlite3 schema (read-only reference, canonical md5
/// 78b3bdbf038db3b927056106efdf91af, corroborated via archaeology MCP
/// query_sql):
///   instrument_sounds(id, item_id, midi) — 199 rows; MIDI families agree
///   exactly with the item categories (24/25/26/29/30/32 → Lute 80,
///   66/68/69/70/71/72/73/74/105/106 → Flute 81; zero rows outside 80/81).
///
/// Loader: MusicManager.LoadInstrumentSounds reads the table into an
/// itemId → MIDI dictionary (neighbor pattern: ItemManager item_proc_bindings
/// + SpecialtyManager SQLite loads). Wiring: PlayUserMusic resolves the
/// equipped instrument by TemplateId first and maps the MIDI family to the
/// play buff; unlisted instruments keep the legacy category switch verbatim.
/// </summary>
[ParallelLimiter<AAEmu.UnitTests.Game.Housing.SequentialParallelLimit>]
[NotInParallel]
public class MusicInstrumentSoundTests
{
    // Rig-local ids — never collide with other suites' fixtures.
    private const uint MappedLuteItemId = 9301;    // CategoryId None, midi 24 → LutePlay via data
    private const uint MappedFluteItemId = 9303;   // CategoryId None, midi 73 → FlutePlay via data
    private const uint UnmappedFluteItemId = 9302; // CategoryId Flute, no mapping → legacy switch
    private const uint RigOwnerIdBase = 930000;

    private const uint LutePlayBuffId = (uint)BuffConstants.LutePlay;   // 6177
    private const uint FlutePlayBuffId = (uint)BuffConstants.FlutePlay; // 6176

    #region Manager accessor

    [Test]
    public async Task TryGetInstrumentMidi_KnownItemId_ReturnsMappedMidi()
    {
        var manager = CreateMusicManager();
        SetPrivateField(manager, "_instrumentSounds", new Dictionary<uint, uint> { { MappedLuteItemId, 24 } });

        var found = manager.TryGetInstrumentMidi(MappedLuteItemId, out var midi);

        await Assert.That(found).IsTrue();
        await Assert.That(midi).IsEqualTo(24u);
    }

    [Test]
    public async Task TryGetInstrumentMidi_UnknownItemId_ReturnsFalse()
    {
        var manager = CreateMusicManager();
        SetPrivateField(manager, "_instrumentSounds", new Dictionary<uint, uint> { { MappedLuteItemId, 24 } });

        var found = manager.TryGetInstrumentMidi(UnmappedFluteItemId, out var midi);

        await Assert.That(found).IsFalse();
        await Assert.That(midi).IsEqualTo(0u);
    }

    [Test]
    public async Task TryGetInstrumentMidi_BeforeLoad_ReturnsFalse()
    {
        var manager = CreateMusicManager();

        var found = manager.TryGetInstrumentMidi(MappedLuteItemId, out _);

        await Assert.That(found).IsFalse();
    }

    [Test]
    public async Task LoadInstrumentSounds_CanonicalData_Loads199RowsWithKnownMappings()
    {
        var dbPath = Path.Combine(AAEmu.Commons.IO.FileManager.AppPath, "Data", "compact.sqlite3");
        if (!File.Exists(dbPath))
        {
            Console.WriteLine("[MusicInstrument] SKIPPED — canonical compact.sqlite3 not present");
            return;
        }

        var manager = CreateMusicManager();
        manager.LoadInstrumentSounds();

        await Assert.That(manager.InstrumentSoundCount).IsEqualTo(199);
        await Assert.That(manager.TryGetInstrumentMidi(20488, out var eperium)).IsTrue();
        await Assert.That(eperium).IsEqualTo(29u); // Eperium Wave Lute
        await Assert.That(manager.TryGetInstrumentMidi(26134, out var ashkaltum)).IsTrue();
        await Assert.That(ashkaltum).IsEqualTo(73u); // Ashkaltum flute
        await Assert.That(manager.TryGetInstrumentMidi(6177, out var first)).IsTrue();
        await Assert.That(first).IsEqualTo(24u);
    }

    #endregion

    #region MIDI family classifier

    [Test]
    public async Task GetPlayBuffForMidi_CanonicalStringFamily_ReturnsLutePlay()
    {
        foreach (var midi in new uint[] { 24, 25, 26, 29, 30, 32 })
            await Assert.That(PlayUserMusic.GetPlayBuffForMidi(midi)).IsEqualTo(LutePlayBuffId);
    }

    [Test]
    public async Task GetPlayBuffForMidi_CanonicalWindFamily_ReturnsFlutePlay()
    {
        foreach (var midi in new uint[] { 66, 68, 69, 70, 71, 72, 73, 74, 105, 106 })
            await Assert.That(PlayUserMusic.GetPlayBuffForMidi(midi)).IsEqualTo(FlutePlayBuffId);
    }

    [Test]
    public async Task GetPlayBuffForMidi_UnlistedMidi_ReturnsNullForCategoryFallback()
    {
        foreach (var midi in new uint[] { 0, 27, 28, 31, 33, 65, 67, 75, 104, 107, 127 })
            await Assert.That(PlayUserMusic.GetPlayBuffForMidi(midi)).IsNull();
    }

    #endregion

    #region Playback rig — mapping reaches the character through the real effect path

    [Test]
    public async Task Execute_MappedLuteItem_AppliesLutePlayFromData()
    {
        SeedSingletons(new Dictionary<uint, uint> { { MappedLuteItemId, 24 } });
        using var rig = new MusicRig();

        var item = EquipItem(rig.Character, MappedLuteItemId, (int)EquipmentItemSlot.Musical);
        new PlayUserMusic().Execute(rig.Character, null!, rig.Character, null!, null!, null!, null!, DateTime.UtcNow, 0, 0, 0, 0);

        await Assert.That(item).IsNotNull();
        await Assert.That(rig.Character.Buffs.CheckBuff(LutePlayBuffId)).IsTrue();
        await Assert.That(rig.Character.Buffs.CheckBuff(FlutePlayBuffId)).IsFalse();
    }

    [Test]
    public async Task Execute_MappedFluteItem_AppliesFlutePlayFromData()
    {
        SeedSingletons(new Dictionary<uint, uint> { { MappedFluteItemId, 73 } });
        using var rig = new MusicRig();

        var item = EquipItem(rig.Character, MappedFluteItemId, (int)EquipmentItemSlot.Musical);
        new PlayUserMusic().Execute(rig.Character, null!, rig.Character, null!, null!, null!, null!, DateTime.UtcNow, 0, 0, 0, 0);

        await Assert.That(item).IsNotNull();
        await Assert.That(rig.Character.Buffs.CheckBuff(FlutePlayBuffId)).IsTrue();
        await Assert.That(rig.Character.Buffs.CheckBuff(LutePlayBuffId)).IsFalse();
    }

    [Test]
    public async Task Execute_UnmappedFluteCategory_FallsBackToLegacySwitch()
    {
        SeedSingletons(new Dictionary<uint, uint>());
        using var rig = new MusicRig();

        var item = EquipItem(rig.Character, UnmappedFluteItemId, (int)EquipmentItemSlot.Musical);
        new PlayUserMusic().Execute(rig.Character, null!, rig.Character, null!, null!, null!, null!, DateTime.UtcNow, 0, 0, 0, 0);

        await Assert.That(item).IsNotNull();
        await Assert.That(rig.Character.Buffs.CheckBuff(FlutePlayBuffId)).IsTrue();
    }

    [Test]
    public async Task Execute_NoInstrumentEquipped_AppliesNoBuff()
    {
        SeedSingletons(new Dictionary<uint, uint>());
        using var rig = new MusicRig();

        new PlayUserMusic().Execute(rig.Character, null!, rig.Character, null!, null!, null!, null!, DateTime.UtcNow, 0, 0, 0, 0);

        await Assert.That(rig.Character.Buffs.CheckBuff(LutePlayBuffId)).IsFalse();
        await Assert.That(rig.Character.Buffs.CheckBuff(FlutePlayBuffId)).IsFalse();
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

    /// <summary>Rig character that drops all broadcast packets.</summary>
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

    /// <summary>Headless playback rig: character + equipped instrument, disposable.</summary>
    private sealed class MusicRig : IDisposable
    {
        public Character Character { get; } = BuildCharacter();

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
            Name = "MusicRig01",
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

    private static void SeedSingletons(Dictionary<uint, uint> instrumentSounds)
    {
        if (!SingletonSeeded(typeof(Singleton<ItemManager>)))
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
        buffs.TryAdd(FlutePlayBuffId, new BuffTemplate { Id = FlutePlayBuffId, Duration = 1, Kind = BuffKind.Good });
        buffs.TryAdd(LutePlayBuffId, new BuffTemplate { Id = LutePlayBuffId, Duration = 1, Kind = BuffKind.Good });

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

        // This suite's own data: instrument templates (additive — rig-local ids
        // never collide) on whichever ItemManager instance is live.
        var itemManager = ItemManager.Instance;
        var templatesField = typeof(ItemManager).GetField("_templates", flags)!;
        var templates = (Dictionary<uint, ItemTemplate>)templatesField.GetValue(itemManager)!;
        if (templates == null)
        {
            templates = [];
            templatesField.SetValue(itemManager, templates);
        }
        templates[MappedLuteItemId] = new BodyPartTemplate { Id = MappedLuteItemId, Name = "Rig Lute", SlotTypeId = (uint)EquipmentItemSlotType.Instrument, CategoryId = (int)ItemCategory.None };
        templates[MappedFluteItemId] = new BodyPartTemplate { Id = MappedFluteItemId, Name = "Rig Flute", SlotTypeId = (uint)EquipmentItemSlotType.Instrument, CategoryId = (int)ItemCategory.None };
        templates[UnmappedFluteItemId] = new BodyPartTemplate { Id = UnmappedFluteItemId, Name = "Rig Unmapped Flute", SlotTypeId = (uint)EquipmentItemSlotType.Instrument, CategoryId = (int)ItemCategory.Flute };
        EnsureDictionaryField(itemManager, "_itemProcBindings");
        EnsureDictionaryField(itemManager, "_itemUnitModifiers");
        EnsureDictionaryField(itemManager, "_equipItemSets");

        var itemGameData = ItemGameData.Instance;
        if (typeof(ItemGameData).GetField("_itemGradeBuffs", flags)!.GetValue(itemGameData) == null)
        {
            typeof(ItemGameData).GetField("_itemGradeBuffs", flags)!
                .SetValue(itemGameData, new Dictionary<uint, Dictionary<byte, uint>>());
        }

        // This suite owns the MusicManager surface: no other suite drives it.
        if (!SingletonSeeded(typeof(Singleton<MusicManager>)))
        {
            typeof(Singleton<MusicManager>).GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)!
                .SetValue(null, CreateMusicManager());
        }
        var musicManager = MusicManager.Instance;
        SetPrivateField(musicManager, "_uploadQueue", new Dictionary<uint, SongData>());
        SetPrivateField(musicManager, "_allSongs", new Dictionary<uint, SongData>());
        SetPrivateField(musicManager, "_midiCache", new Dictionary<uint, byte[]>());
        SetPrivateField(musicManager, "_instrumentSounds", new Dictionary<uint, uint>(instrumentSounds));
    }

    private static void EnsureDictionaryField(object obj, string fieldName)
    {
        var field = obj.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance)!;
        if (field.GetValue(obj) != null)
            return;
        var dictType = typeof(Dictionary<,>).MakeGenericType(field.FieldType.GetGenericArguments());
        field.SetValue(obj, Activator.CreateInstance(dictType));
    }

    private static bool SingletonSeeded(Type singletonBase)
        => singletonBase.GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)
            ?.GetValue(null) != null;

    private static MusicManager CreateMusicManager()
    {
        return new MusicManager(Mock.Of<IMusicIdManager>().Object, Mock.Of<IItemManager>().Object);
    }

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