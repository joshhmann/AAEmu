using AAEmu.Commons.Utils;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Containers;
using AAEmu.Game.Models.Game.Mate;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Units;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace AAEmu.UnitTests.Game.GameData;

/// <summary>
/// Tests for mate equipment legality checks based on the mate_equip_* tables
/// </summary>
public class MateGameDataEquipLegalityTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly MateGameData _gameData;

    public MateGameDataEquipLegalityTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TABLE npc_mount_skills (id INT, npc_id INT, mount_skill_id INT);
                CREATE TABLE mount_skills (id INT, name TEXT, skill_id INT);
                CREATE TABLE mount_attached_skills (id INT, mount_skill_id INT, attach_point_id INT, skill_id INT);
                CREATE TABLE mate_equip_packs (id INT, name TEXT);
                CREATE TABLE mate_equip_pack_groups (id INT, npc_id INT, mate_equip_pack_id INT);
                CREATE TABLE mate_equip_pack_items (id INT, mate_equip_pack_id INT, item_id INT);
                CREATE TABLE mate_equip_slot_packs (id int PRIMARY KEY, name TEXT, head NUM, chest NUM, waist NUM, feet NUM);

                -- slot pack 1 = riding summon (head/waist/feet), 2 = battle summon (head/chest/feet), 3 = pet (none)
                INSERT INTO mate_equip_slot_packs VALUES (1, 'riding', 't', 'f', 't', 't');
                INSERT INTO mate_equip_slot_packs VALUES (2, 'battle', 't', 't', 'f', 't');
                INSERT INTO mate_equip_slot_packs VALUES (3, 'pet', 'f', 'f', 'f', 'f');

                INSERT INTO mate_equip_packs VALUES (1, 'pack one');
                INSERT INTO mate_equip_packs VALUES (2, 'pack two');

                -- npc 100 may use pack one, npc 200 may use pack two
                INSERT INTO mate_equip_pack_groups VALUES (1, 100, 1);
                INSERT INTO mate_equip_pack_groups VALUES (2, 200, 2);

                -- items 10 and 11 belong to pack one, item 20 belongs to pack two
                INSERT INTO mate_equip_pack_items VALUES (1, 1, 10);
                INSERT INTO mate_equip_pack_items VALUES (2, 1, 11);
                INSERT INTO mate_equip_pack_items VALUES (3, 2, 20);
                """;
            cmd.ExecuteNonQuery();
        }

        _gameData = new MateGameData();
        _gameData.Load(_connection);
    }

    public void Dispose()
    {
        _connection.Dispose();
    }

    [Test]
    public async Task Load_PopulatesSlotPacks()
    {
        var riding = _gameData.GetMateEquipSlotPack(1);
        await Assert.That(riding).IsNotNull();
        await Assert.That(riding.Head).IsTrue();
        await Assert.That(riding.Chest).IsFalse();
        await Assert.That(riding.Waist).IsTrue();
        await Assert.That(riding.Feet).IsTrue();
    }

    [Test]
    public async Task LegalEquip_HeadGearOnRidingMount_IsAllowed()
    {
        await Assert.That(_gameData.IsMateEquipAllowed(100, 1, 10, EquipmentItemSlot.Head)).IsTrue();
    }

    [Test]
    public async Task LegalEquip_WaistGearOnRidingMount_IsAllowed()
    {
        await Assert.That(_gameData.IsMateEquipAllowed(200, 2, 20, EquipmentItemSlot.Feet)).IsTrue();
    }

    [Test]
    public async Task WrongSlot_GearForDisabledSlot_IsRefused()
    {
        // riding mounts have chest = f
        await Assert.That(_gameData.IsMateEquipAllowed(100, 1, 10, EquipmentItemSlot.Chest)).IsFalse();
        // battle summons have waist = f
        await Assert.That(_gameData.IsMateEquipAllowed(200, 2, 20, EquipmentItemSlot.Waist)).IsFalse();
    }

    [Test]
    public async Task WrongMateType_ItemFromUnassignedPack_IsRefused()
    {
        // item 10 is bound to pack one, but npc 200 only allows pack two
        await Assert.That(_gameData.IsMateEquipAllowed(200, 2, 10, EquipmentItemSlot.Head)).IsFalse();
        // and the reverse combination
        await Assert.That(_gameData.IsMateEquipAllowed(100, 1, 20, EquipmentItemSlot.Chest)).IsFalse();
    }

    [Test]
    public async Task PetCategory_AllSlotsRefused()
    {
        await Assert.That(_gameData.IsMateEquipAllowed(300, 3, 10, EquipmentItemSlot.Head)).IsFalse();
        await Assert.That(_gameData.IsMateEquipAllowed(300, 3, 10, EquipmentItemSlot.Chest)).IsFalse();
    }

    [Test]
    public async Task NonGearSlot_IsRefused()
    {
        await Assert.That(_gameData.IsMateEquipAllowed(100, 1, 10, EquipmentItemSlot.Mainhand)).IsFalse();
        await Assert.That(_gameData.IsMateEquipAllowed(100, 1, 10, EquipmentItemSlot.Neck)).IsFalse();
    }

    [Test]
    public async Task MissingTableData_FailsClosed()
    {
        // unknown npc -> no pack groups entry
        await Assert.That(_gameData.IsMateEquipAllowed(999, 1, 10, EquipmentItemSlot.Head)).IsFalse();
        // unknown item -> no pack items entry
        await Assert.That(_gameData.IsMateEquipAllowed(100, 1, 999, EquipmentItemSlot.Head)).IsFalse();
        // unknown slot pack id
        await Assert.That(_gameData.IsMateEquipAllowed(100, 42, 10, EquipmentItemSlot.Head)).IsFalse();
        // missing slot pack entirely
        await Assert.That(_gameData.IsMateEquipAllowed(100, 0, 10, EquipmentItemSlot.Head)).IsFalse();
        // zero identifiers
        await Assert.That(_gameData.IsMateEquipAllowed(0, 1, 10, EquipmentItemSlot.Head)).IsFalse();
        await Assert.That(_gameData.IsMateEquipAllowed(100, 1, 0, EquipmentItemSlot.Head)).IsFalse();
    }

    [Test]
    public async Task EmptyTables_NothingIsAllowed()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TABLE npc_mount_skills (id INT, npc_id INT, mount_skill_id INT);
                CREATE TABLE mount_skills (id INT, name TEXT, skill_id INT);
                CREATE TABLE mount_attached_skills (id INT, mount_skill_id INT, attach_point_id INT, skill_id INT);
                CREATE TABLE mate_equip_packs (id INT, name TEXT);
                CREATE TABLE mate_equip_pack_groups (id INT, npc_id INT, mate_equip_pack_id INT);
                CREATE TABLE mate_equip_pack_items (id INT, mate_equip_pack_id INT, item_id INT);
                CREATE TABLE mate_equip_slot_packs (id int PRIMARY KEY, name TEXT, head NUM, chest NUM, waist NUM, feet NUM);
                """;
            cmd.ExecuteNonQuery();
        }

        var emptyGameData = new MateGameData();
        emptyGameData.Load(connection);

        await Assert.That(emptyGameData.IsMateEquipAllowed(100, 1, 10, EquipmentItemSlot.Head)).IsFalse();
    }
}

/// <summary>
/// Tests for the MateEquipmentContainer.CanAccept chokepoint
/// </summary>
[NotInParallel] // touches process-wide SingletonContainer.ServiceProvider + singletons
public sealed class MateEquipmentContainerAcceptTests : IDisposable
{
    private readonly SqliteConnection _connection;

    public MateEquipmentContainerAcceptTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TABLE npc_mount_skills (id INT, npc_id INT, mount_skill_id INT);
                CREATE TABLE mount_skills (id INT, name TEXT, skill_id INT);
                CREATE TABLE mount_attached_skills (id INT, mount_skill_id INT, attach_point_id INT, skill_id INT);
                CREATE TABLE mate_equip_packs (id INT, name TEXT);
                CREATE TABLE mate_equip_pack_groups (id INT, npc_id INT, mate_equip_pack_id INT);
                CREATE TABLE mate_equip_pack_items (id INT, mate_equip_pack_id INT, item_id INT);
                CREATE TABLE mate_equip_slot_packs (id int PRIMARY KEY, name TEXT, head NUM, chest NUM, waist NUM, feet NUM);

                INSERT INTO mate_equip_slot_packs VALUES (1, 'riding', 't', 'f', 't', 't');
                INSERT INTO mate_equip_packs VALUES (1, 'pack one');
                INSERT INTO mate_equip_pack_groups VALUES (1, 100, 1);
                INSERT INTO mate_equip_pack_items VALUES (1, 1, 10);
                """;
            cmd.ExecuteNonQuery();
        }

        var gameData = new MateGameData();
        gameData.Load(_connection);

        // Make MateGameData.Instance resolve to the freshly loaded instance
        typeof(Singleton<MateGameData>)
            .GetField("s_instance", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
            ?.SetValue(null, null);
        var services = new ServiceCollection();
        services.AddSingleton(gameData);
        SingletonContainer.ServiceProvider = services.BuildServiceProvider();
    }

    public void Dispose()
    {
        SingletonContainer.ServiceProvider = null;
        typeof(Singleton<MateGameData>)
            .GetField("s_instance", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
            ?.SetValue(null, null);
        _connection.Dispose();
    }

    private static Mate CreateMate(uint npcTemplateId = 100, int slotPackId = 1)
    {
        return new Mate
        {
            Template = new NpcTemplate { Id = npcTemplateId, MateEquipSlotPackId = slotPackId }
        };
    }

    [Test]
    public async Task CanAccept_LegalMateGear_IsAccepted()
    {
        var mate = CreateMate();
        var item = new EquipItem { TemplateId = 10 };

        await Assert.That(((MateEquipmentContainer)mate.Equipment).CanAccept(item, (int)EquipmentItemSlot.Head)).IsTrue();
    }

    [Test]
    public async Task CanAccept_SlotNotAllowedBySlotPack_IsRefused()
    {
        var mate = CreateMate(); // riding mount: chest not allowed
        var item = new EquipItem { TemplateId = 10 };

        await Assert.That(((MateEquipmentContainer)mate.Equipment).CanAccept(item, (int)EquipmentItemSlot.Chest)).IsFalse();
    }

    [Test]
    public async Task CanAccept_ItemNotBoundToMatePack_IsRefused()
    {
        var mate = CreateMate();
        var item = new EquipItem { TemplateId = 999 };

        await Assert.That(((MateEquipmentContainer)mate.Equipment).CanAccept(item, (int)EquipmentItemSlot.Head)).IsFalse();
    }

    [Test]
    public async Task CanAccept_OutOfRangeSlot_IsRefused()
    {
        var mate = CreateMate();
        var item = new EquipItem { TemplateId = 10 };

        await Assert.That(((MateEquipmentContainer)mate.Equipment).CanAccept(item, byte.MaxValue)).IsFalse();
    }

    [Test]
    public async Task CanAccept_EmptySlot_AlwaysAccepted()
    {
        var mate = CreateMate();

        await Assert.That(((MateEquipmentContainer)mate.Equipment).CanAccept(null, (int)EquipmentItemSlot.Head)).IsTrue();
    }

    [Test]
    public async Task CanAccept_ParentUnitIsNoMate_IsRefused()
    {
        var orphanContainer = new MateEquipmentContainer(0, SlotType.EquipmentMate, false, null);
        var item = new EquipItem { TemplateId = 10 };

        await Assert.That(orphanContainer.CanAccept(item, (int)EquipmentItemSlot.Head)).IsFalse();
    }
}
