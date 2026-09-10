using System.Collections.Concurrent;
using System.Numerics;
using System.Reflection;
using AAEmu.Commons.Models;
using AAEmu.Commons.Utils;
using AAEmu.Commons.Utils.DB;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.Stream;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Funcs;
using AAEmu.Game.Models.Game.DoodadObj.Static;
using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.Housing;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Game.Items.Containers;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.Mails;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.World.Interactions;
using AAEmu.Game.Models.Game.World.Transform;
using AAEmu.UnitTests.Utils.Mocks;
using TUnit.Core.Interfaces;

namespace AAEmu.UnitTests.Game.Housing;

/// <summary>
/// B3 PROPERTY-01 A run: behavior assertions through observable Character/world state
/// (inventories, world doodad registry, demolish mails) — NOT save-file inspection.
///
/// Covers, on the synchronous rig-level engine paths:
///   1. furniture place → pickup conservation (DecorateHouse recipe →
///      RecoverItem.Execute, the F-key pickup path),
///   2. storage deposit → withdraw conservation (Inventory.SplitCofferItems, the
///      CSSplitCofferItemPacket path, both directions),
///   3. item conservation across place → restart → pickup (restart simulated at
///      rig level: world drop + load-recipe restore with persistence armed last,
///      then pickup; the same instance must survive end to end),
///   4. ReturnHouseItemsToOwner (demolish / sale return path: restore-mail,
///      attached-ignore, marker-ignore, detach, destroy, coffer drain),
///   5. the unpaid-tax-demolish deviation pin: the fork deliberately keeps the
///      HouseCannotDemolishUnpaidTax refusal disabled (HousingManager.Demolish,
///      ZeromusXYZ note), so an overdue-tax house still demolishes.
///
/// Context (not re-cited as A proof): R=2 already via M3bExitPersistenceE2eTests
/// N=3 crash cycles; the MySQL housings+doodads row contract is identified in
/// m3-canonical-audit.md §4.1–4.2; M3b exit t_accb1c63. The DB-write seam itself
/// (Doodad.Save / DeleteHouseRowImmediately) stays R territory: every doodad
/// here is non-persistent and MySQL is pointed at a dead port (M3b hermetic
/// guard), so an accidental persistence attempt fails fast instead of touching
/// a real database.
///
/// Place uses the DecorateHouse state recipe (HousingManager.cs:1873-1915)
/// rather than calling DecorateHouse itself, because the method ends with a
/// live Doodad.Save() — the persistence seam covered by the R=2 E2E, not by
/// this A run. Every observable transition the method guarantees (ownership +
/// house binding, item Bag→System move, world spawn) is replicated exactly and
/// asserted through Character inventory + world registry state.
/// </summary>
[ParallelLimiter<SequentialParallelLimit>]
[NotInParallel]
public class PropertyItemConservationTests
{
    private const uint TestInstanceId = 7;

    // Unique-to-this-suite template/design ids (own ranges; additive seeding).
    private const uint FurnitureDoodadId = 5101;   // plain furniture, recoverable
    private const uint CofferDoodadId = 5102;      // coffer furniture, capacity 20
    private const uint PlantDoodadId = 5104;       // NOT a decoration (no design row)
    private const uint NoRestoreDoodadId = 5105;   // decoration whose item does NOT restore
    private const uint RecoverGroupId = 5201;      // Start group of 5101
    private const uint RecoverFuncId = 5301;       // DoodadFuncRecoverItem row
    private const uint FurnitureDesignId = 7101;   // design → doodad 5101
    private const uint CofferDesignId = 7102;      // design → doodad 5102
    private const uint NoRestoreDesignId = 7103;   // design → doodad 5105
    private const uint FurnitureItemTemplateId = 6101;
    private const uint CofferShellItemTemplateId = 6102;
    private const uint NoRestoreItemTemplateId = 6103;
    private const uint CofferContentTemplateId = 6201;
    private const uint DepositItemTemplateId = 6202;
    private const uint ForSaleMarkerTemplateId = 6760; // HousingManager.ForSaleMarkerDoodadId

    private object _previousDoodadManager;
    private object _previousItemManager;
    private object _previousHousingManager;
    private object _previousWorldManager;
    private object _previousMailManager;
    private object _previousNameManager;
    private object _previousHousingGameData;

    private ItemManager _itemManager;
    private DoodadManager _doodadManager;
    private HousingManager _housingManager;
    private MailManager _mailManager;
    private NameManager _nameManager;
    private WorldInstance _world;
    private uint _nextItemId = 930001;

    [Before(Test)]
    public void SetUp()
    {
        _previousDoodadManager = GetSingletonInstance<DoodadManager>();
        _previousItemManager = GetSingletonInstance<ItemManager>();
        _previousHousingManager = GetSingletonInstance<HousingManager>();
        _previousWorldManager = GetSingletonInstance<WorldManager>();
        _previousMailManager = GetSingletonInstance<MailManager>();
        _previousNameManager = GetSingletonInstance<NameManager>();
        _previousHousingGameData = GetSingletonInstance<HousingGameData>();

        // Hermetic guard: any accidental Save()/DELETE must fail FAST
        // (connection refused), never touch a real database.
        MySQL.SetConfiguration(new MySqlConnectionSettings { Host = "127.0.0.1", Port = 1 });

        SeedItemManager();
        SeedQuestManagerMissingOnly();
        ContainerIdManager.Instance.Initialize(false);
        SeedWorld();
        SeedDoodadManager();
        SeedNameAndMailManagers();
        SeedHousingManager();
        SeedHousingGameData();
    }

    [After(Test)]
    public void TearDown()
    {
        SetSingletonInstance(typeof(Singleton<DoodadManager>), _previousDoodadManager);
        SetSingletonInstance(typeof(Singleton<ItemManager>), _previousItemManager);
        SetSingletonInstance(typeof(Singleton<HousingManager>), _previousHousingManager);
        SetSingletonInstance(typeof(Singleton<WorldManager>), _previousWorldManager);
        SetSingletonInstance(typeof(Singleton<MailManager>), _previousMailManager);
        SetSingletonInstance(typeof(Singleton<NameManager>), _previousNameManager);
        SetSingletonInstance(typeof(Singleton<HousingGameData>), _previousHousingGameData);
        MySQL.SetConfiguration(null); // restore default (localhost:3306)
    }


    // ================================================================ PLACE → PICKUP

    [Test]
    public async Task PlaceFurniture_NonStackableItemFromBag_DoodadSpawnsAndItemConservedInSystem()
    {
        // Arrange — owner with 1× furniture item in the bag + owned house.
        var character = MakeCharacter(91001, 92001, "B3Prop_Place", 0xB101);
        var house = MakeHouse(8101, 3101, character, new Vector3(1000f, 2000f, 50f));
        var item = MakeBagItem(character, FurnitureItemTemplateId);

        // Act — DecorateHouse recipe (HousingManager.cs:1873-1915), rig adaptation:
        // IsPersistent stays false (no MySQL; persistence is R territory).
        var doodad = PlaceFurnitureRecipe(character, house, item, new Vector3(3f, -2f, 1f));

        // Assert — observable Character + world state: the item left the bag exactly
        // once, lives in System exactly once, and the world holds the bound doodad.
        await Assert.That(doodad).IsNotNull();
        await Assert.That(doodad.TemplateId).IsEqualTo(FurnitureDoodadId);
        await Assert.That(doodad.OwnerType).IsEqualTo(DoodadOwnerType.Housing);
        await Assert.That(doodad.OwnerDbId).IsEqualTo(house.Id);
        await Assert.That(doodad.ItemId).IsEqualTo(item.Id);
        await Assert.That(character.Inventory.Bag.GetItemByItemId(item.Id)).IsNull();
        await Assert.That(character.Inventory.SystemContainer.GetItemByItemId(item.Id)).IsNotNull();
        await Assert.That(_world.GetDoodadByHouseDbId(house.Id)).Contains(doodad);
    }

    [Test]
    public async Task RecoverItem_PlacedHouseFurniture_ItemReturnsToBagAndDoodadLeavesWorld()
    {
        // Arrange — placed via the same recipe the place test pins.
        var character = MakeCharacter(91002, 92002, "B3Prop_Pickup", 0xB102);
        var house = MakeHouse(8102, 3102, character, new Vector3(1100f, 2000f, 50f));
        var item = MakeBagItem(character, FurnitureItemTemplateId);
        var doodad = PlaceFurnitureRecipe(character, house, item, new Vector3(1f, 1f, 0f));
        await Assert.That(character.Inventory.SystemContainer.GetItemByItemId(item.Id)).IsNotNull();

        // Act — the real F-key pickup path (CSLootOpenBagPacket/skill routing converges here).
        new RecoverItem().Execute(character, null, doodad, null, 0, 0, null);

        // Assert — conservation: the SAME instance is back in the bag, gone from
        // System, and the world no longer holds the doodad.
        await Assert.That(character.Inventory.Bag.GetItemByItemId(item.Id)).IsNotNull();
        await Assert.That(character.Inventory.SystemContainer.GetItemByItemId(item.Id)).IsNull();
        await Assert.That(doodad.ItemId).IsEqualTo(0u);
        await Assert.That(_world.GetDoodad(doodad.ObjId)).IsNull();
        await Assert.That(_world.GetDoodadByHouseDbId(house.Id)).DoesNotContain(doodad);
    }

    [Test]
    public async Task PlaceRestartPickup_FurnitureItem_ItemConservedEndToEndAcrossSimulatedRestart()
    {
        // Arrange — place; capture the exact payload Doodad.Save() would persist.
        var character = MakeCharacter(91003, 92003, "B3Prop_Restart", 0xB103);
        var house = MakeHouse(8103, 3103, character, new Vector3(1200f, 2000f, 50f));
        var item = MakeBagItem(character, FurnitureItemTemplateId);
        var placed = PlaceFurnitureRecipe(character, house, item, new Vector3(3f, -2f, 1f));
        placed.Transform.Local.ApplyFromQuaternion(Quaternion.CreateFromYawPitchRoll(0f, 0f, MathF.PI / 2f));
        var placedWorldPos = placed.Transform.World.Position;
        var placedWorldRot = placed.Transform.World.ToQuaternion();
        var saved = new SavedDoodadState(placed);

        // Act 1 — simulated restart: the in-memory world is dropped WITHOUT touching
        // inventories (server shutdown keeps System rows); the boot load recipe then
        // restores every field and arms persistence LAST (M3b-1 ordering).
        _world.RemoveObject(placed);
        await Assert.That(_world.GetDoodad(placed.ObjId)).IsNull();
        await Assert.That(character.Inventory.SystemContainer.GetItemByItemId(item.Id)).IsNotNull();

        var loaded = DoodadManager.Instance.Create(_world, 0, saved.TemplateId, null, true);
        await Assert.That(loaded).IsNotNull();
        loaded.FuncGroupId = saved.FuncGroupId;
        loaded.OwnerId = saved.OwnerId;
        loaded.OwnerType = saved.OwnerType;
        loaded.AttachPoint = saved.AttachPoint;
        loaded.OwnerDbId = saved.OwnerDbId;
        loaded.ItemId = saved.ItemId;
        loaded.ItemTemplateId = saved.ItemTemplateId;
        loaded.ParentObjId = house.ObjId;
        loaded.ParentObj = house;
        loaded.Transform.Parent = house.Transform;
        loaded.Transform.Local.SetPosition(saved.X, saved.Y, saved.Z);
        loaded.Transform.Local.SetRotation(saved.Roll, saved.Pitch, saved.Yaw);
        // Rig keeps IsPersistent=false (no MySQL); production arms it here, after
        // the full restore — the ordering the M3b-1 fix pins.
        _world.AddObject(loaded);

        // Assert — world transform + attachment survived the restart sim.
        await Assert.That(loaded.Transform.World.Position.X).IsEqualTo(placedWorldPos.X).Within(0.001f);
        await Assert.That(loaded.Transform.World.Position.Y).IsEqualTo(placedWorldPos.Y).Within(0.001f);
        await Assert.That(loaded.Transform.World.Position.Z).IsEqualTo(placedWorldPos.Z).Within(0.001f);
        await Assert.That(MathF.Abs(Quaternion.Dot(placedWorldRot, loaded.Transform.World.ToQuaternion()))).IsGreaterThan(0.999f);
        await Assert.That(loaded.OwnerDbId).IsEqualTo(house.Id);

        // Act 2 — pickup after re-entry on the restored doodad.
        new RecoverItem().Execute(character, null, loaded, null, 0, 0, null);

        // Assert — end-to-end conservation of the single instance.
        await Assert.That(character.Inventory.Bag.GetItemByItemId(item.Id)).IsNotNull();
        await Assert.That(character.Inventory.SystemContainer.GetItemByItemId(item.Id)).IsNull();
        await Assert.That(_world.GetDoodad(loaded.ObjId)).IsNull();
    }

    // ================================================================ STORAGE

    [Test]
    public async Task SplitCofferItems_DepositThenWithdraw_ItemConservedBetweenBagAndCoffer()
    {
        // Arrange — owner with 1× item in the bag + empty house coffer.
        var character = MakeCharacter(91004, 92004, "B3Prop_Coffer", 0xB104);
        var house = MakeHouse(8104, 3104, character, new Vector3(1300f, 2000f, 50f));
        var coffer = MakeHouseCoffer(house, character.Id);
        var cofferDbId = coffer.GetItemContainerId();
        var item = MakeBagItem(character, DepositItemTemplateId);

        // Act 1 — deposit on the real packet path (CSSplitCofferItemPacket →
        // Inventory.SplitCofferItems: Bag → Trade).
        var deposited = character.Inventory.SplitCofferItems(
            1, item.Id, 0, SlotType.Inventory, (byte)item.Slot, SlotType.Trade, 0, cofferDbId);

        // Assert — the item moved exactly once; nothing duplicated or lost.
        await Assert.That(deposited).IsTrue();
        await Assert.That(character.Inventory.Bag.GetItemByItemId(item.Id)).IsNull();
        await Assert.That(coffer.ItemContainer.GetItemByItemId(item.Id)).IsNotNull();

        // Act 2 — withdraw on the same path reversed (Trade → Bag).
        var withdrawn = character.Inventory.SplitCofferItems(
            1, item.Id, 0, SlotType.Trade, (byte)item.Slot, SlotType.Inventory, 0, cofferDbId);

        // Assert — round-trip conservation of the single instance.
        await Assert.That(withdrawn).IsTrue();
        await Assert.That(coffer.ItemContainer.GetItemByItemId(item.Id)).IsNull();
        await Assert.That(character.Inventory.Bag.GetItemByItemId(item.Id)).IsNotNull();
    }

    // ================================================================ RETURN + DEMOLISH

    [Test]
    public async Task ReturnHouseItemsToOwner_MixedFurniture_RestoreItemsMailedOthersDetachedOrDeleted()
    {
        // Arrange — offline owner (world lookup returns null → mail-slot path, no
        // live-inventory touch) + house with one of each furniture fate.
        var character = MakeCharacter(91005, 92005, "B3Prop_Return", 0xB105);
        _nameManager.AddCharacter(character.Id, character.Name, character.AccountId);
        var house = MakeHouse(8105, 3105, character, new Vector3(1400f, 2000f, 50f));
        RegisterHouse(house);

        // Restore shell (coffer) + its System-linked item + one stored content item.
        var shell = MakeRawHouseDoodad(CofferDoodadId, house);
        var shellCoffer = (DoodadCoffer)shell;
        shellCoffer.InitializeCoffer(character.Id);
        var shellItem = MakeSystemItem(character, CofferShellItemTemplateId);
        shell.ItemId = shellItem.Id;
        shell.ItemTemplateId = CofferShellItemTemplateId;
        var contentItem = MakeItem(CofferContentTemplateId);
        shellCoffer.ItemContainer.AddOrMoveExistingItem(ItemTaskType.Gm, contentItem);

        // Attached door/window: ignored by the return path.
        var door = MakeRawHouseDoodad(FurnitureDoodadId, house);
        door.AttachPoint = AttachPointKind.HealPoint0;

        // For-sale marker: ignored by the return path.
        var marker = MakeRawHouseDoodad(FurnitureDoodadId, house);
        marker.TemplateId = ForSaleMarkerTemplateId;

        // Non-furniture (no decoration design, e.g. plant): detached, kept in world.
        var plant = MakeRawHouseDoodad(PlantDoodadId, house);

        // Non-restore furniture without a bound item: destroyed, never mailed.
        var noRestore = MakeRawHouseDoodad(NoRestoreDoodadId, house);
        noRestore.ItemTemplateId = NoRestoreItemTemplateId;

        // Act — the private demolish/sale return path (failedToPayTax skips the tax
        // refund branch; no house design is seeded so no design mail is produced —
        // the mail below carries ONLY the furniture return).
        var method = typeof(HousingManager).GetMethod(
            "ReturnHouseItemsToOwner", BindingFlags.NonPublic | BindingFlags.Instance)!;
        method.Invoke(_housingManager, [house, true, false, null]);

        // Assert — one Demolish mail with exactly the shell + coffer content.
        await Assert.That(_mailManager.AllPlayerMails.Count).IsEqualTo(1);
        var mail = _mailManager.AllPlayerMails.Values.Single();
        await Assert.That(mail.MailType).IsEqualTo(MailType.Demolish);
        await Assert.That(mail.Header.ReceiverId).IsEqualTo(character.Id);
        await Assert.That(mail.Body.Attachments.Select(i => i.Id))
            .Contains(shellItem.Id);
        await Assert.That(mail.Body.Attachments.Select(i => i.Id))
            .Contains(contentItem.Id);
        await Assert.That(mail.Body.Attachments.Count).IsEqualTo(2);
        await Assert.That(shellItem.SlotType).IsEqualTo(SlotType.Mail);

        // Assert — world fates: restore shell deleted, door + marker untouched,
        // plant detached but kept, non-restore destroyed.
        await Assert.That(_world.GetDoodad(shell.ObjId)).IsNull();
        await Assert.That(_world.GetDoodad(door.ObjId)).IsNotNull();
        await Assert.That(door.AttachPoint).IsEqualTo(AttachPointKind.HealPoint0);
        await Assert.That(_world.GetDoodad(marker.ObjId)).IsNotNull();
        await Assert.That(_world.GetDoodad(plant.ObjId)).IsNotNull();
        await Assert.That(plant.OwnerDbId).IsEqualTo(0u);
        await Assert.That(plant.ParentObj).IsNull();
        await Assert.That(_world.GetDoodad(noRestore.ObjId)).IsNull();
        await Assert.That(_world.GetDoodadByHouseDbId(house.Id).Count).IsEqualTo(2);
    }

    [Test]
    public async Task Demolish_HouseWithOverdueTax_ProceedsAndClearsOwnerAsDocumentedDeviation()
    {
        // Arrange — house whose tax is overdue (canonical 1.2 would refuse a manual
        // demolish / auto-demolish after default; the fork keeps the
        // HouseCannotDemolishUnpaidTax refusal DISABLED — HousingManager.cs
        // commented block, ZeromusXYZ note; m3-canonical-audit §4.2 records this
        // as a documented fork choice, not a defect).
        var character = MakeCharacter(91006, 92006, "B3Prop_Demolish", 0xB106);
        _nameManager.AddCharacter(character.Id, character.Name, character.AccountId);
        var house = MakeHouse(8106, 3106, character, new Vector3(1500f, 2000f, 50f));
        // TaxDueDate is computed as ProtectionEndDate - 7d: a lapsed protection
        // date models the unpaid-tax state (canonical 1.2 would refuse/auto-demolish).
        house.ProtectionEndDate = DateTime.UtcNow.AddDays(-9);
        RegisterHouse(house);

        // Act — null connection bypasses the owner check (same as the owner path);
        // failedToPayTax skips the tax refund so no mail/money branches run.
        _housingManager.Demolish(null, house, true, false);

        // Assert — the documented deviation: demolish PROCEEDED despite unpaid tax
        // (owner cleared). Re-enabling the refusal would fail this pin.
        await Assert.That(house.OwnerId).IsEqualTo(0u);
        await Assert.That(house.CoOwnerId).IsEqualTo(0u);
        await Assert.That(house.AccountId).IsEqualTo(0u);
        await Assert.That(house.SellPrice).IsEqualTo(0u);
        await Assert.That(house.Permission).IsEqualTo(HousingPermission.Public);
        await Assert.That(_mailManager.AllPlayerMails.Count).IsEqualTo(0);
    }

    // ================================================================ rig helpers

    private static object GetSingletonInstance<T>() where T : class
    {
        return typeof(Singleton<T>).GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
    }

    private static void SetSingletonInstance(Type singletonType, object instance)
    {
        singletonType.GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)?.SetValue(null, instance);
    }

    private static void SetField(object target, string fieldName, object value)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Field '{fieldName}' not found on {target.GetType().Name}");
        field.SetValue(target, value);
    }

    private void SeedItemManager()
    {
        _itemManager = new ItemManager(
            Mock.Of<ISkillManager>().Object,
            new CountingItemIdManager(),
            Mock.Of<IContainerIdManager>().Object,
            Mock.Of<ILocalizationManager>().Object,
            Mock.Of<ITaskManager>().Object,
            Mock.Of<IWorldManager>().Object);
        SetField(_itemManager, "_allPersistentContainers", new ConcurrentDictionary<ulong, ItemContainer>());
        SetField(_itemManager, "_allItems", new ConcurrentDictionary<ulong, Item>());
        SetField(_itemManager, "_removedItems", new List<ulong>());
        SetField(_itemManager, "_templates", new Dictionary<uint, ItemTemplate>
        {
            [FurnitureItemTemplateId] = new ItemTemplate { Id = FurnitureItemTemplateId, MaxCount = 1, BindType = ItemBindType.Normal },
            [CofferShellItemTemplateId] = new ItemTemplate { Id = CofferShellItemTemplateId, MaxCount = 1, BindType = ItemBindType.Normal },
            [NoRestoreItemTemplateId] = new ItemTemplate { Id = NoRestoreItemTemplateId, MaxCount = 1, BindType = ItemBindType.Normal },
            [CofferContentTemplateId] = new ItemTemplate { Id = CofferContentTemplateId, MaxCount = 1, BindType = ItemBindType.Normal },
            [DepositItemTemplateId] = new ItemTemplate { Id = DepositItemTemplateId, MaxCount = 1, BindType = ItemBindType.Normal },
        });
        SetSingletonInstance(typeof(Singleton<ItemManager>), _itemManager);
    }

    private static void SeedQuestManagerMissingOnly()
    {
        // Missing-only shared fixture (MailTaxLifecycleTests convention): item moves
        // fire OnAcquiredItem → QuestManager.Instance.DoItemsAcquiredEvents, which
        // must resolve without touching a database.
        if (GetSingletonInstance<QuestManager>() is null)
        {
            var questManager = new QuestManager(Mock.Of<ITaskManager>().Object, Mock.Of<IZoneManager>().Object);
            SetField(questManager, "_componentTemplates", new Dictionary<uint, QuestComponentTemplate>());
            SetField(questManager, "_groupItems", new Dictionary<uint, List<uint>>());
            SetField(questManager, "_groupNpcs", new Dictionary<uint, List<uint>>());
            SetSingletonInstance(typeof(Singleton<QuestManager>), questManager);
        }
    }

    private void SeedWorld()
    {
        var worldManager = new WorldManager(
            Mock.Of<ITickManager>().Object,
            Mock.Of<IWorldIdManager>().Object,
            new Lazy<IZoneManager>(() => Mock.Of<IZoneManager>().Object),
            new Lazy<IIndunManager>(() => Mock.Of<IIndunManager>().Object),
            new Lazy<IFamilyManager>(() => Mock.Of<IFamilyManager>().Object));
        SetField(worldManager, "_worlds", new ConcurrentDictionary<uint, WorldInstance>());
        SetSingletonInstance(typeof(Singleton<WorldManager>), worldManager);

        _world = new WorldInstance(new WorldTemplate { Id = TestInstanceId, Name = "b3_prop_world" }, 0, false, TestInstanceId);
        _world.Regions = new Region[16, 16];
        var worlds = (ConcurrentDictionary<uint, WorldInstance>)typeof(WorldManager)
            .GetField("_worlds", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(worldManager);
        worlds?.TryAdd(_world.Id, _world);
    }

    private void SeedDoodadManager()
    {
        _doodadManager = new DoodadManager(
            new FakeObjectIdManager(0xA201),
            Mock.Of<IDoodadIdManager>().Object,
            Mock.Of<IItemManager>().Object,
            new Lazy<IHousingManager>(() => Mock.Of<IHousingManager>().Object),
            Mock.Of<ISusManager>().Object);

        // Recoverable furniture template: Start group carrying DoodadFuncRecoverItem
        // (the F-key pickup func RecoverItem.Execute resolves).
        var recoverGroup = new DoodadFuncGroups
        {
            Id = RecoverGroupId,
            Almighty = FurnitureDoodadId,
            GroupKindId = DoodadFuncGroups.DoodadFuncGroupKind.Start
        };
        var furnitureTemplate = new DoodadTemplate { Id = FurnitureDoodadId };
        furnitureTemplate.FuncGroups.Add(recoverGroup);

        SetField(_doodadManager, "_templates", new Dictionary<uint, DoodadTemplate>
        {
            [FurnitureDoodadId] = furnitureTemplate,
            [CofferDoodadId] = new DoodadCofferTemplate { Id = CofferDoodadId, Capacity = 20 },
            [PlantDoodadId] = new DoodadTemplate { Id = PlantDoodadId },
            [NoRestoreDoodadId] = new DoodadTemplate { Id = NoRestoreDoodadId },
        });
        SetField(_doodadManager, "_allFuncGroups", new Dictionary<uint, DoodadFuncGroups>
        {
            [RecoverGroupId] = recoverGroup
        });
        SetField(_doodadManager, "_funcsByGroups", new Dictionary<uint, List<DoodadFunc>>
        {
            [RecoverGroupId] =
            [
                new DoodadFunc
                {
                    GroupId = RecoverGroupId,
                    FuncId = RecoverFuncId,
                    FuncType = "DoodadFuncRecoverItem",
                    NextPhase = -1,
                    SkillId = 0
                }
            ]
        });
        SetField(_doodadManager, "_funcsById", new Dictionary<uint, DoodadFunc>());
        SetField(_doodadManager, "_funcTemplates", new Dictionary<string, Dictionary<uint, DoodadFuncTemplate>>
        {
            ["DoodadFuncRecoverItem"] = new Dictionary<uint, DoodadFuncTemplate>
            {
                [RecoverFuncId] = new DoodadFuncRecoverItem { Id = RecoverFuncId }
            }
        });
        SetField(_doodadManager, "_phaseFuncs", new Dictionary<uint, List<DoodadPhaseFunc>>());
        SetField(_doodadManager, "_phaseFuncTemplates", new Dictionary<string, Dictionary<uint, DoodadPhaseFuncTemplate>>());
        SetSingletonInstance(typeof(Singleton<DoodadManager>), _doodadManager);
    }

    private void SeedNameAndMailManagers()
    {
        _nameManager = new NameManager();
        _nameManager.Load([], [], []);
        SetSingletonInstance(typeof(Singleton<NameManager>), _nameManager);

        var mailIdManager = new MailIdManager();
        mailIdManager.Initialize();
        // HousingManager is constructed next; the lazy resolves the field afterwards.
        _mailManager = new MailManager(
            mailIdManager,
            _nameManager,
            Mock.Of<IItemManager>().Object,
            Mock.Of<ITaskManager>().Object,
            Mock.Of<IWorldManager>().Object,
            new Lazy<IHousingManager>(() => _housingManager),
            Mock.Of<ILocalizationManager>().Object);
        _mailManager._allPlayerMails = [];
        SetSingletonInstance(typeof(Singleton<MailManager>), _mailManager);
    }

    private void SeedHousingManager()
    {
        _housingManager = new HousingManager(
            Mock.Of<IObjectIdManager>().Object,
            Mock.Of<IFactionManager>().Object,
            Mock.Of<ILocalizationManager>().Object,
            Mock.Of<IWorldManager>().Object,
            Mock.Of<ITaskManager>().Object,
            Mock.Of<ISkillManager>().Object,
            Mock.Of<IHousingIdManager>().Object,
            Mock.Of<IHousingTldManager>().Object,
            _itemManager,
            Mock.Of<IMailManager>().Object,
            _nameManager,
            Mock.Of<IZoneManager>().Object,
            _doodadManager,
            Mock.Of<IUccManager>().Object);
        SetField(_housingManager, "_houses", new Dictionary<uint, House>());
        SetField(_housingManager, "_housesTl", new Dictionary<ushort, House>());
        SetSingletonInstance(typeof(Singleton<HousingManager>), _housingManager);
    }

    private void SeedHousingGameData()
    {
        var gameData = new HousingGameData();
        SetSingletonInstance(typeof(Singleton<HousingGameData>), gameData);
        var decorations = (Dictionary<uint, HousingDecoration>)typeof(HousingGameData)
            .GetField("_housingDecorations", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(gameData);
        decorations?.TryAdd(FurnitureDesignId, new HousingDecoration { Id = FurnitureDesignId, DoodadId = FurnitureDoodadId });
        decorations?.TryAdd(CofferDesignId, new HousingDecoration { Id = CofferDesignId, DoodadId = CofferDoodadId });
        decorations?.TryAdd(NoRestoreDesignId, new HousingDecoration { Id = NoRestoreDesignId, DoodadId = NoRestoreDoodadId });
        var itemDecorations = (List<ItemHousingDecoration>)typeof(HousingGameData)
            .GetField("_housingItemHousingDecorations", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(gameData);
        itemDecorations?.Add(new ItemHousingDecoration { Id = 1, ItemId = FurnitureItemTemplateId, DesignId = FurnitureDesignId, Restore = true });
        itemDecorations?.Add(new ItemHousingDecoration { Id = 2, ItemId = CofferShellItemTemplateId, DesignId = CofferDesignId, Restore = true });
        itemDecorations?.Add(new ItemHousingDecoration { Id = 3, ItemId = NoRestoreItemTemplateId, DesignId = NoRestoreDesignId, Restore = false });
    }

    private CharacterMock MakeCharacter(uint id, uint accountId, string name, uint objId)
    {
        var character = new CharacterMock
        {
            AccountId = accountId,
            Id = id,
            Name = name,
            ObjId = objId,
            Money = 1000,
            NumInventorySlots = 50,
            NumBankSlots = 50
        };
        character.Inventory = new Inventory(character);
        var session = new PacketCaptureSession();
        var connection = new GameConnection(session) { ActiveChar = character };
        character.Connection = connection;
        character.ParentWorld = _world;
        return character;
    }

    private House MakeHouse(uint id, ushort tlId, CharacterMock owner, Vector3 position)
    {
        var house = new House
        {
            Id = id,
            ObjId = 0xC000 + id,
            TlId = tlId,
            TemplateId = 9101,
            Name = $"b3_prop_house_{id}",
            OwnerId = owner.Id,
            CoOwnerId = owner.Id,
            AccountId = owner.AccountId,
            Permission = HousingPermission.Public,
            AllowRecover = true,
            PlaceDate = DateTime.UtcNow,
            ProtectionEndDate = DateTime.UtcNow.AddDays(14)
        };
        house.Template = new HousingTemplate
        {
            Id = 9101,
            Name = "b3-prop-test-house",
            MainModelId = 1,
            HousingBindingDoodad = [],
            AlwaysPublic = true
        };
        house.CurrentStep = -1;
        house.Transform = new Transform(house, null, position, new Vector3(0f, 0f, 0f));
        house.ParentWorld = _world;
        _world.AddObject(house);
        return house;
    }

    private void RegisterHouse(House house)
    {
        var houses = (Dictionary<uint, House>)typeof(HousingManager)
            .GetField("_houses", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(_housingManager);
        houses?[house.Id] = house;
        var housesTl = (Dictionary<ushort, House>)typeof(HousingManager)
            .GetField("_housesTl", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(_housingManager);
        housesTl?[house.TlId] = house;
    }

    private ItemMock MakeItem(uint templateId)
    {
        var template = _itemManager.GetTemplate(templateId)
            ?? throw new InvalidOperationException($"Item template {templateId} not seeded");
        var item = new ItemMock(_nextItemId++, template);
        _itemManager.AddItem(item);
        return item;
    }

    private ItemMock MakeBagItem(CharacterMock character, uint templateId)
    {
        var item = MakeItem(templateId);
        character.Inventory.Bag.AddOrMoveExistingItem(ItemTaskType.Gm, item);
        return item;
    }

    private ItemMock MakeSystemItem(CharacterMock character, uint templateId)
    {
        var item = MakeItem(templateId);
        character.Inventory.SystemContainer.AddOrMoveExistingItem(ItemTaskType.Gm, item);
        return item;
    }

    /// <summary>
    /// The DecorateHouse state recipe (HousingManager.cs:1873-1915) minus the final
    /// Doodad.Save(): creation + house-relative transform + item linkage + the
    /// Bag→System move for a non-stackable (MaxCount ≤ 1) furniture item.
    /// </summary>
    private Doodad PlaceFurnitureRecipe(CharacterMock character, House house, Item item, Vector3 localPos)
    {
        var doodad = DoodadManager.Instance.Create(_world, 0, FurnitureDoodadId, house, true);
        doodad.Transform.Parent = house.Transform;
        doodad.Transform.Local.SetPosition(localPos.X, localPos.Y, localPos.Z);
        doodad.Transform.Local.ApplyFromQuaternion(Quaternion.Identity);
        doodad.ItemTemplateId = item.TemplateId;
        doodad.ItemId = item.Id;
        doodad.OwnerDbId = house.Id;
        doodad.OwnerId = character.Id;
        doodad.ParentObjId = house.ObjId;
        doodad.ParentObj = house;
        doodad.AttachPoint = AttachPointKind.None;
        doodad.OwnerType = DoodadOwnerType.Housing;
        _world.AddObject(doodad);
        character.Inventory.SystemContainer.AddOrMoveExistingItem(ItemTaskType.DoodadCreate, item);
        return doodad;
    }

    private Doodad MakeRawHouseDoodad(uint templateId, House house)
    {
        var doodad = DoodadManager.Instance.Create(_world, 0, templateId, house, true);
        doodad.AttachPoint = AttachPointKind.None;
        _world.AddObject(doodad);
        return doodad;
    }

    private DoodadCoffer MakeHouseCoffer(House house, uint ownerId)
    {
        var doodad = DoodadManager.Instance.Create(_world, 0, CofferDoodadId, house, true);
        var coffer = (DoodadCoffer)doodad;
        _world.AddObject(coffer);
        coffer.InitializeCoffer(ownerId);
        return coffer;
    }

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

    /// <summary>Snapshot of the fields Doodad.Save() persists for a house doodad.</summary>
    private sealed class SavedDoodadState
    {
        public readonly uint TemplateId;
        public readonly uint FuncGroupId;
        public readonly uint OwnerId;
        public readonly DoodadOwnerType OwnerType;
        public readonly AttachPointKind AttachPoint;
        public readonly uint OwnerDbId;
        public readonly ulong ItemId;
        public readonly uint ItemTemplateId;
        public readonly float X, Y, Z, Roll, Pitch, Yaw;

        public SavedDoodadState(Doodad doodad)
        {
            TemplateId = doodad.TemplateId;
            FuncGroupId = doodad.FuncGroupId;
            OwnerId = doodad.OwnerId;
            OwnerType = doodad.OwnerType;
            AttachPoint = doodad.AttachPoint;
            OwnerDbId = doodad.OwnerDbId;
            ItemId = doodad.ItemId;
            ItemTemplateId = doodad.ItemTemplateId;
            X = doodad.Transform.Local.Position.X;
            Y = doodad.Transform.Local.Position.Y;
            Z = doodad.Transform.Local.Position.Z;
            Roll = doodad.Transform.Local.Rotation.X;
            Pitch = doodad.Transform.Local.Rotation.Y;
            Yaw = doodad.Transform.Local.Rotation.Z;
        }
    }
}
