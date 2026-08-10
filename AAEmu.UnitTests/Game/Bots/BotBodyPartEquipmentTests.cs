using System.Reflection;
using AAEmu.Commons.Network;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Char.Templates;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Containers;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.UnitTests.Game.Bots;

/// <summary>
/// P0 hotfix t_d0889187 — FINAL visibility layer. Rig evidence chain:
///
///  FAIL-BEFORE (prod, 2026-08-08 22:00 UTC): after hotfix #4 (blob healed
///  to 231B type=Face), bot rows Citizen01-03 STILL have ZERO items in every
///  container (item_containers exist, items table has no rows for owners
///  3-5) while a real human row (Asssaa, id 6) carries body-part items:
///  nu_m_face00 (19838) @ slot 19, hair (24133) @ slot 20, nu_m_nude (536)
///  @ slot 24 — plus starter gear. The 1.2 client builds the character
///  mesh from the EQUIPMENT section of SCUnitStatePacket: Inventory_Equip
///  writes item.TemplateId for body-part slots 19-26 and the NPC path
///  forces SetType(Skin) when validFlags &lt;= 0 — "no body and no face"
///  (SCUnitStatePacket.cs:406). A unit with an empty equipment container
///  renders name tag + position but no body.
///
///  DEMO BODY SOURCE (Josh direction 2026-08-08): bots are players → they
///  need player-like appearance. FOR NOW replicate Asssaa's exact
///  appearance (unit_model_params 231B with 733 at bytes 2-5 + her equipped
///  items) so bodies render immediately. Full factory = separate card.
///
///  PASS-AFTER: BotAppearanceDefaults.BuildDefault(race, gender, 10) emits
///  the EXACT Asssaa blob (head `03 DD020000 01000000 ...`) and
///  EquipTemplateBodyParts() equips her exact 10 items, so the
///  SCUnitStatePacket equipment section carries non-zero template ids and
///  the client can assemble the mesh.
/// </summary>
[NotInParallel]
public class BotBodyPartEquipmentTests
{
    // ------------------------------------------------------------------ fail-before evidence

    [Test]
    public async Task TemplateBodyParts_Model10_AreCanonicalHumanItems()
    {
        // The canonical model-10 body parts (compact.sqlite3
        // item_body_parts, slot_type_id 23/24/28) are the exact items
        // Asssaa (rendering human) has equipped. This is the ground truth
        // the equip helper must reproduce.
        var template = BuildTemplate(10);

        await Assert.That(template.Items[0]).IsEqualTo(19838u); // nu_m_face00 -> slot 19 (Face)
        await Assert.That(template.Items[1]).IsEqualTo(34113u); // hair029        -> slot 20 (Hair)
        await Assert.That(template.Items[5]).IsEqualTo(536u);   // nu_m_nude      -> slot 24 (Body)
    }

    [Test]
    public async Task ProvisionedBot_BeforeFix_HasEmptyEquipment_FailBefore()
    {
        // The pre-hotfix provisioning shape (hotfix #4 code path): a
        // Character built like BuildProvisionedCharacter does (Inventory
        // created, no body parts equipped) serializes an equipment section
        // with validFlags=0 — every body-part slot (19-26) writes 0 — the
        // exact "no body and no face" state the client cannot render.
        SeedFixtureSingletons();
        var character = BuildCharacterWithInventory();

        var bodyPartCount = CountEquippedBodyParts(character);
        var wire = SerializeUnitState(character);

        // Fail-before: zero equipped body parts AND zero template ids from
        // the canonical set on the wire.
        await Assert.That(bodyPartCount).IsEqualTo(0);
        await Assert.That(EquipmentSectionHasCanonicalIds(wire)).IsFalse();
    }

    // ------------------------------------------------------------------ pass-after: canonical default (t_555ed207 class fix)

    [Test]
    public async Task BuildDefault_Model10_SerializesCanonicalFace_NotDemoBlob()
    {
        // t_555ed207 class fix: BuildDefault(modelId==10) must return the
        // canonical per-model builder — NOT Asssaa's embedded demo blob.
        // The old demo special case collapsed every Nuian-male bot to one
        // identical look whenever any code path called BuildDefault. The
        // canonical default keeps the render-proven 231-byte type=Face
        // structure with the canonical first hair/skin ids (compact.sqlite3).
        var modelParams = BotAppearanceDefaults.BuildDefault(Race.Nuian, Gender.Male, 10);

        var bytes = modelParams.Write(new PacketStream()).GetBytes();

        await Assert.That(bytes.Length).IsEqualTo(231);
        await Assert.That(bytes[0]).IsEqualTo((byte)UnitCustomModelType.Face);
        // Canonical hair/skin (1/1 per compact.sqlite3 MIN(id) for model 10)
        // — NOT the demo marker 733 (0x2DD) at bytes 2-5.
        var (hair, skin) = BotAppearanceDefaults.CanonicalColorsFor(10);
        await Assert.That(BitConverter.ToUInt32(bytes, 1)).IsEqualTo(hair);
        await Assert.That(BitConverter.ToUInt32(bytes, 5)).IsEqualTo(skin);
        await Assert.That(BitConverter.ToUInt32(bytes, 1)).IsNotEqualTo(733u);

        // Round-trip: Read(Write(x)) == x (DB blob path uses Read()).
        var stream = (PacketStream)bytes;
        var read = new UnitCustomModelParams();
        read.Read(stream);
        await Assert.That(read.Write(new PacketStream()).GetBytes().SequenceEqual(bytes)).IsTrue();
    }

    [Test]
    public async Task BuildDefault_OtherModels_StillSerializeFaceStructure()
    {
        // Non-demo models keep the canonical 231-byte Face structure.
        uint[] models = [11, 16, 17, 18, 19, 20, 21];

        foreach (var modelId in models)
        {
            var bytes = BotAppearanceDefaults.BuildDefault(Race.Nuian, Gender.Male, modelId)
                .Write(new PacketStream()).GetBytes();

            await Assert.That(bytes.Length).IsEqualTo(231);
            await Assert.That(bytes[0]).IsEqualTo((byte)UnitCustomModelType.Face);
            await Assert.That(BitConverter.ToUInt32(bytes, 1) > 0).IsTrue(); // hair non-zero
            await Assert.That(BitConverter.ToUInt32(bytes, 5) > 0).IsTrue(); // skin non-zero
        }
    }

    [Test]
    public async Task ValidDistinctBlob_IsNotDegenerate_AdoptPathLeavesItAlone()
    {
        // t_555ed207 regression: the adopt path's ONLY blob heal is the
        // IsDegenerate check (1-byte type=None). A valid 231-byte blob —
        // whether the canonical default or a factory-distinct look — must
        // NOT be flagged, so adopted rows keep their stored look across
        // reboots. The old `needDemoBlob` guard failed exactly here: it
        // replaced ANY non-demo blob (including valid distinct ones) with
        // the demo appearance on every boot.
        var canonical = BotAppearanceDefaults.BuildDefault(Race.Nuian, Gender.Male, 10);
        var factoryLike = new UnitCustomModelParams(UnitCustomModelType.Face)
            .SetHairColorId(42)
            .SetSkinColorId(7)
            .SetModelId(0)
            .SetFace(new FaceModel { NormalMapWeight = 1.0f });

        await Assert.That(BotAppearanceDefaults.IsDegenerate(canonical)).IsFalse();
        await Assert.That(BotAppearanceDefaults.IsDegenerate(factoryLike)).IsFalse();

        // Sanity: the degenerate shape itself still trips the heal.
        await Assert.That(BotAppearanceDefaults.IsDegenerate(new UnitCustomModelParams())).IsTrue();
    }

    // ------------------------------------------------------------------ pass-after: equip helper

    [Test]
    public async Task EquipTemplateBodyParts_Model10_EquipsAsssaaItems()
    {
        SeedFixtureSingletons();
        var character = BuildCharacterWithInventory();
        var template = BuildTemplate(10);

        var equipped = BotAppearanceDefaults.EquipTemplateBodyParts(character, template);

        // Asssaa's 10 items (demo body source).
        await Assert.That(equipped).IsEqualTo(10);
        await Assert.That(character.Inventory.Equipment.GetItemBySlot(19)?.TemplateId).IsEqualTo(19838u);
        await Assert.That(character.Inventory.Equipment.GetItemBySlot(20)?.TemplateId).IsEqualTo(24133u);
        await Assert.That(character.Inventory.Equipment.GetItemBySlot(24)?.TemplateId).IsEqualTo(536u);
        // Starter gear slots too.
        await Assert.That(character.Inventory.Equipment.GetItemBySlot(2)?.TemplateId).IsEqualTo(23387u);
        await Assert.That(character.Inventory.Equipment.GetItemBySlot(4)?.TemplateId).IsEqualTo(23388u);
        await Assert.That(character.Inventory.Equipment.GetItemBySlot(6)?.TemplateId).IsEqualTo(23390u);
        await Assert.That(character.Inventory.Equipment.GetItemBySlot(15)?.TemplateId).IsEqualTo(5569u);
        await Assert.That(character.Inventory.Equipment.GetItemBySlot(16)?.TemplateId).IsEqualTo(6152u);
        await Assert.That(character.Inventory.Equipment.GetItemBySlot(17)?.TemplateId).IsEqualTo(6127u);
        await Assert.That(character.Inventory.Equipment.GetItemBySlot(18)?.TemplateId).IsEqualTo(6177u);
    }

    // ------------------------------------------------------------------ wire-level: equipment reaches the client

    [Test]
    public async Task SCUnitStatePacket_EquippedBot_EquipmentSectionCarriesTemplateIds()
    {
        SeedFixtureSingletons();
        // Distinct character ids — the equipment container is keyed by
        // ownerId, so two same-id characters would share one container. Use
        // the rig's high id range so no other suite class collides.
        var bare = BuildCharacterWithInventory(id: 900100);
        var equipped = BuildCharacterWithInventory(id: 900101);
        var equippedCount = BotAppearanceDefaults.EquipTemplateBodyParts(equipped, BuildTemplate(10));

        var bareWire = SerializeUnitState(bare);
        var equippedWire = SerializeUnitState(equipped);

        // Body parts serialize as a 4-byte template id (BodyPart.Write), so
        // the equipment section carries the canonical ids ONLY on the
        // equipped frame — the exact data the client needs to assemble the
        // mesh. An empty equipment container has no template ids on the wire
        // (validFlags=0 — the "no body and no face" state).
        await Assert.That(equippedCount).IsEqualTo(10);
        await Assert.That(EquipmentSectionHasCanonicalIds(equippedWire)).IsTrue();
        await Assert.That(EquipmentSectionHasCanonicalIds(bareWire)).IsFalse();
    }

    // ------------------------------------------------------------------ helpers

    private static CharacterTemplate BuildTemplate(uint modelId)
    {
        return new CharacterTemplate
        {
            Race = Race.Nuian,
            Gender = Gender.Male,
            ModelId = modelId,
            Items = CanonicalBodyParts[modelId]
        };
    }

    /// <summary>
    /// Compact.sqlite3 item_body_parts → CharacterTemplate.Items (slot_type_id
    /// 23-29 → Items[0..6], last-by-id per slot, npc_only ignored by the
    /// loader). Verified live 2026-08-08.
    /// </summary>
    private static readonly IReadOnlyDictionary<uint, uint[]> CanonicalBodyParts =
        new Dictionary<uint, uint[]>
        {
            [10] = [19838, 34113, 0, 0, 0, 536, 0],
            [11] = [19839, 34300, 0, 0, 0, 539, 0],
            [16] = [23713, 34111, 0, 0, 0, 1142, 0],
            [17] = [23714, 34302, 0, 0, 0, 1128, 0],
            [18] = [23715, 34114, 0, 0, 0, 548, 0],
            [19] = [23716, 34301, 0, 0, 0, 553, 0],
            [20] = [20117, 34117, 0, 0, 562, 559, 564],
            [21] = [20118, 34307, 0, 0, 568, 565, 0],
        };

    private static Character BuildCharacterWithInventory(uint id = 900001)
    {
        var character = new Character(new UnitCustomModelParams())
        {
            Id = id,
            Name = "Citizen01",
            Level = 5,
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

    private static int CountEquippedBodyParts(Character character)
    {
        var count = 0;
        for (var s = (int)EquipmentItemSlot.Face; s <= (int)EquipmentItemSlot.Beard; s++)
        {
            if (character.Inventory?.Equipment?.GetItemBySlot(s) != null)
                count++;
        }
        return count;
    }

    private static byte[] SerializeUnitState(Character character)
    {
        var packet = new SCUnitStatePacket(character);
        return packet.Write(new PacketStream()).GetBytes();
    }

    /// <summary>
    /// True when the frame carries ANY of the canonical body-part template
    /// ids (face/hair/body for models 10/11) as a 4-byte little-endian run —
    /// an empty equipment section contains none.
    /// </summary>
    private static bool EquipmentSectionHasCanonicalIds(byte[] wire)
    {
        uint[] canonicalIds = [19838, 34113, 536, 19839, 34300, 539, 23387, 23388, 23390, 5569, 6152, 6127, 6177, 24133];
        foreach (var id in canonicalIds)
        {
            var le = new[] { (byte)(id & 0xFF), (byte)((id >> 8) & 0xFF), (byte)((id >> 16) & 0xFF), (byte)((id >> 24) & 0xFF) };
            for (var i = 0; i <= wire.Length - 4; i++)
            {
                if (wire[i] == le[0] && wire[i + 1] == le[1] && wire[i + 2] == le[2] && wire[i + 3] == le[3])
                    return true;
            }
        }
        return false;
    }

    // ---------------------------------------------------------------- singleton seeding (mirrors HeadlessSessionProvisioningTests + BotAppearanceDefaultsTests)

    /// <summary>
    /// Rig characters use this high id range — never collides with other
    /// suite classes' owner ids, and lets the per-test cleanup below remove
    /// exactly what THIS rig registered.
    /// </summary>
    private const uint RigOwnerIdBase = 900000;

    [After(Test)]
    public void AfterTest_CleanupFixtureContainers()
    {
        // This rig registers persistent containers in the SHARED ItemManager
        // singleton (Inventory ctor → GetItemContainerForCharacter). Leaving
        // them behind breaks other suite classes: a later
        // ContainerIdManager.Initialize(true) resets the id counter while
        // _allPersistentContainers still holds our keys → duplicate-key 65536
        // (t_6bad0654 full-suite hazard). Remove exactly the containers we
        // created (and their items) after every test. Blob-only tests never
        // seed ItemManager — the hook must tolerate an unseeded singleton.
        if (!SingletonSeeded(typeof(Singleton<ItemManager>)))
            return;
        CleanupFixtureContainers();
    }

    private static void CleanupFixtureContainers()
    {
        var itemManager = ItemManager.Instance;
        var containersField = typeof(ItemManager).GetField("_allPersistentContainers",
            BindingFlags.NonPublic | BindingFlags.Instance);
        var itemsField = typeof(ItemManager).GetField("_allItems",
            BindingFlags.NonPublic | BindingFlags.Instance);
        if (containersField?.GetValue(itemManager) is not Dictionary<ulong, ItemContainer> containers)
            return;

        var toRemove = containers.Where(kv => kv.Value.OwnerId >= RigOwnerIdBase).Select(kv => kv.Key).ToList();
        foreach (var key in toRemove)
        {
            var container = containers[key];
            if (itemsField?.GetValue(itemManager) is Dictionary<ulong, Item> allItems)
            {
                foreach (var item in container.Items.ToList())
                    allItems.Remove(item.Id);
            }
            containers.Remove(key);
        }
    }

    private static void SeedFixtureSingletons()
    {
        SetSingletonIfMissing(typeof(Singleton<ItemManager>), BuildFixtureItemManager());
        // Initialize(false): no-op when already initialized (full-suite
        // safety — t_6bad0654 forceReset pitfall).
        ContainerIdManager.Instance.Initialize(false);
        SeedPacketSurface();
    }

    /// <summary>
    /// Seeds the singletons SCUnitStatePacket serialization touches
    /// (SkillManager buffs 8000011/8000012 + BuffGameData + EffectTaskManager)
    /// — the same surface BotAppearanceDefaultsTests.SeedPacketSurface
    /// establishes. Missing-only guards, never replaces (t_4f11a519).
    /// </summary>
    private static void SeedPacketSurface()
    {
        if (!SingletonSeeded(typeof(Singleton<SkillManager>)))
        {
            SeedSingleton(typeof(Singleton<SkillManager>),
                new SkillManager(Mock.Of<IAnimationManager>().Object, Mock.Of<IPlotManager>().Object));
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

        var buffs = (Dictionary<uint, BuffTemplate>)typeof(SkillManager).GetField("_buffs", flags)!.GetValue(manager)!;
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

        // Equipping gear evaluates grade buffs (Unit.ApplyEquipEffects →
        // ItemGameData.GetItemBuff); seed the registry so equip doesn't NRE.
        var itemGameData = ItemGameData.Instance;
        var itemGradeBuffs = (Dictionary<uint, Dictionary<byte, uint>>)typeof(ItemGameData)
            .GetField("_itemGradeBuffs", flags)!.GetValue(itemGameData)!;
        if (itemGradeBuffs == null)
        {
            typeof(ItemGameData).GetField("_itemGradeBuffs", flags)!
                .SetValue(itemGameData, new Dictionary<uint, Dictionary<byte, uint>>());
        }

        if (!SingletonSeeded(typeof(Singleton<EffectTaskManager>)))
        {
            SeedSingleton(typeof(Singleton<EffectTaskManager>),
                new EffectTaskManager(Mock.Of<ITaskManager>().Object));
        }

        // Equipping items fires QuestManager.Instance.DoItemsAcquiredEvents
        // (Inventory.OnAcquiredItem) — seed with an empty quest manager so
        // the equip path doesn't hit the DI singleton resolution. Missing-only
        // guard; never replaces an established singleton (t_4f11a519).
        if (!SingletonSeeded(typeof(Singleton<QuestManager>)))
        {
            var questManager = new QuestManager(Mock.Of<ITaskManager>().Object, Mock.Of<IZoneManager>().Object);
            SetPrivateField(questManager, "_componentTemplates", new Dictionary<uint, QuestComponentTemplate>());
            SetPrivateField(questManager, "_groupItems", new Dictionary<uint, List<uint>>());
            SetPrivateField(questManager, "_groupNpcs", new Dictionary<uint, List<uint>>());
            SeedSingleton(typeof(Singleton<QuestManager>), questManager);
        }
    }

    private static bool SingletonSeeded(Type singletonBase)
        => singletonBase.GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)
            ?.GetValue(null) != null;

    private static void SeedSingleton(Type singletonBase, object instance)
    {
        var field = singletonBase.GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)
                    ?? throw new InvalidOperationException($"Cannot locate singleton field on {singletonBase.Name}");
        if (field.GetValue(null) == null)
            field.SetValue(null, instance);
    }

    private static ItemManager BuildFixtureItemManager()
    {
        var itemId = 0ul;
        var itemIdManager = new CountingItemIdManager();

        var itemManager = new ItemManager(
            Mock.Of<ISkillManager>().Object,
            itemIdManager,
            Mock.Of<IContainerIdManager>().Object,
            Mock.Of<ILocalizationManager>().Object,
            Mock.Of<ITaskManager>().Object,
            Mock.Of<IWorldManager>().Object);

        // _allPersistentContainers: the Inventory ctor resolves
        // GetItemContainerForCharacter against it — a null registry NREs the
        // ordinary Character construction path.
        SetPrivateField(itemManager, "_allPersistentContainers", new Dictionary<ulong, ItemContainer>());
        SetPrivateField(itemManager, "_allItems", new Dictionary<ulong, Item>());
        SetPrivateField(itemManager, "_removedItems", new List<ulong>());

        // _templates: ItemManager.Create resolves the demo item templates.
        // Body parts are BodyPartTemplate with the item_body_parts
        // slot_type_id; armor/weapons use the plain ItemTemplate default
        // ClassType (typeof(Item)) which EquipmentContainer accepts when the
        // preferred slot is explicit (CanAccept only needs the template for
        // slot-type inference on auto-slot; explicit slot passes through).
        var templates = new Dictionary<uint, ItemTemplate>();
        var demoItems = new (uint Id, uint SlotTypeId)[]
        {
            (19838, 23), // nu_m_face00 -> Face (slot 19)
            (24133, 24), // hair         -> Hair (slot 20)
            (536, 28),   // nu_m_nude    -> Body (slot 24)
            (23387, 3),  // chest
            (23388, 5),  // legs
            (23390, 7),  // feet
            (5569, 14),  // mainhand
            (6152, 15),  // offhand
            (6127, 18),  // ranged
            (6177, 21),  // musical
        };
        foreach (var (id, slotTypeId) in demoItems)
        {
            templates[id] = new BodyPartTemplate { Id = id, SlotTypeId = slotTypeId };
        }
        SetPrivateField(itemManager, "_templates", templates);

        return itemManager;
    }

    /// <summary>Hand-rolled IItemIdManager — increments from 1 (TUnit.Mocks
    /// has no Setup/Returns equivalent for this shape).</summary>
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

    private static void SetPrivateField(object instance, string fieldName, object value)
    {
        var field = instance.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance)
                    ?? throw new InvalidOperationException($"Cannot locate field {fieldName} on {instance.GetType().Name}");
        field.SetValue(instance, value);
    }

    private static void SetSingletonIfMissing(Type singletonBase, object instance)
    {
        var field = singletonBase.GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)
                    ?? throw new InvalidOperationException($"Cannot locate singleton field on {singletonBase.Name}");
        if (field.GetValue(null) != null)
            return; // never replace an established singleton (t_4f11a519)
        field.SetValue(null, instance);
    }
}
