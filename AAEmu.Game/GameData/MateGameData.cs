using AAEmu.Commons.Utils;
using AAEmu.Game.GameData.Framework;
using AAEmu.Game.Models.Game.DoodadObj.Static;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Mate;
using AAEmu.Game.Utils.DB;

using Microsoft.Data.Sqlite;
using NLog;

namespace AAEmu.Game.GameData;

[GameData]
public class MateGameData : Singleton<MateGameData>, IGameDataLoader
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    private Dictionary<uint, NpcMountSkills> _npcMountSkills = [];
    private Dictionary<uint, MountSkills> _mountSkills = [];
    private Dictionary<uint, MountAttachedSkills> _mountAttachedSkills = [];

    // Mate equipment legality data (mate_equip_* tables)
    private Dictionary<uint, string> _mateEquipPacks = [];
    private Dictionary<uint, HashSet<uint>> _mateEquipPackIdsByNpc = []; // npc template id -> allowed pack ids
    private Dictionary<uint, HashSet<uint>> _mateEquipPackIdsByItem = []; // item template id -> pack ids containing it
    private Dictionary<uint, MateEquipSlotPack> _mateEquipSlotPacks = [];

    /// <summary>
    /// Gets a list of pet skill Ids
    /// </summary>
    /// <param name="id"></param>
    /// <returns></returns>
    public List<uint> GetMateSkills(uint id)
    {
        var template = new List<uint>();

        foreach (var value in _npcMountSkills.Values)
            if (value.NpcId == id && !template.Contains(value.MountSkillId))
                template.Add(value.MountSkillId);

        return template;
    }

    /// <summary>
    /// Get the associated rider skill for a given mountSkill
    /// </summary>
    /// <param name="mateSkill">The skill the mate used</param>
    /// <param name="attachPoint">The attachPoint the player is currently on</param>
    /// <returns></returns>
    public uint GetMountAttachedSkills(uint mateSkill, AttachPointKind attachPoint)
    {
        var id = 0u;
        var skill = 0u;

        // Find the mountSkillId for this mate's skill
        foreach (var ms in _mountSkills)
        {
            if (ms.Value.SkillId != mateSkill)
                continue;
            id = ms.Key;
            break;
        }

        // Find the player skill based on the mountSkillId
        foreach (var mas in _mountAttachedSkills)
        {
            if (mas.Value.MountSkillId != id || mas.Value.AttachPointId != attachPoint)
                continue;
            skill = mas.Value.SkillId;
            break;
        }

        return skill;
    }

    /// <summary>
    /// Gets MountSkillId for use with Slaves
    /// </summary>
    /// <param name="slaveSkillId"></param>
    /// <returns></returns>
    public uint GetMountSkillIdForSkill(uint slaveSkillId)
    {
        foreach (var ms in _mountSkills.Values)
        {
            if (ms.SkillId == slaveSkillId)
                return ms.Id;
        }

        return 0;
    }

    /// <summary>
    /// Gets the mate equipment slot pack definition for the given id, if loaded
    /// </summary>
    public MateEquipSlotPack GetMateEquipSlotPack(uint id)
    {
        return _mateEquipSlotPacks.GetValueOrDefault(id);
    }

    /// <summary>
    /// Checks if an item template is allowed to be equipped in the given slot on a mate with the
    /// given npc template. Uses both the per-mate-category slot gate (mate_equip_slot_packs) and
    /// the per-npc item pack membership (mate_equip_pack_groups + mate_equip_pack_items).
    /// Fails closed: when either table has no data for the requested combination, the equip is refused.
    /// </summary>
    /// <param name="npcTemplateId">Template Id of the mate's Npc template</param>
    /// <param name="mateEquipSlotPackId">MateEquipSlotPackId of the mate's Npc template</param>
    /// <param name="itemTemplateId">Template Id of the equipment item</param>
    /// <param name="targetSlot">Target equipment slot on the mate</param>
    /// <returns>true when this combination is legal according to the loaded tables</returns>
    public bool IsMateEquipAllowed(uint npcTemplateId, int mateEquipSlotPackId, uint itemTemplateId, EquipmentItemSlot targetSlot)
    {
        // Fail closed on missing identifiers
        if (npcTemplateId == 0 || itemTemplateId == 0)
            return false;

        // Gate 1: the mate's category must explicitly allow this slot
        if (mateEquipSlotPackId <= 0)
            return false; // no slot pack data for this mate -> refuse

        var slotPack = GetMateEquipSlotPack((uint)mateEquipSlotPackId);
        if (slotPack == null)
            return false; // referenced slot pack is not present in mate_equip_slot_packs -> refuse

        var slotAllowed = targetSlot switch
        {
            EquipmentItemSlot.Head => slotPack.Head,
            EquipmentItemSlot.Chest => slotPack.Chest,
            EquipmentItemSlot.Waist => slotPack.Waist,
            EquipmentItemSlot.Feet => slotPack.Feet,
            _ => false // mates can only wear head/chest/waist/feet gear
        };
        if (!slotAllowed)
            return false;

        // Gate 2: the item must belong to at least one pack assigned to this mate's npc template
        if (!_mateEquipPackIdsByNpc.TryGetValue(npcTemplateId, out var npcPacks) || npcPacks.Count == 0)
            return false; // this mate has no equip packs assigned -> refuse

        if (!_mateEquipPackIdsByItem.TryGetValue(itemTemplateId, out var itemPacks) || itemPacks.Count == 0)
            return false; // item is not part of any mate equip pack -> refuse

        return npcPacks.Overlaps(itemPacks);
    }

    /// <summary>
    /// Loads the game db data for pets
    /// </summary>
    /// <param name="connection"></param>
    public void Load(SqliteConnection connection)
    {
        _npcMountSkills = [];
        _mountSkills = [];
        _mountAttachedSkills = [];

        _mateEquipPacks = [];
        _mateEquipPackIdsByNpc = [];
        _mateEquipPackIdsByItem = [];
        _mateEquipSlotPacks = [];

        #region MateTables

        // Npc Mount skills
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT * FROM npc_mount_skills";
            command.Prepare();
            using (var reader = new SQLiteWrapperReader(command.ExecuteReader()))
            {
                while (reader.Read())
                {
                    var template = new NpcMountSkills
                    {
                        Id = reader.GetUInt32("id"),
                        NpcId = reader.GetUInt32("npc_id"),
                        MountSkillId = reader.GetUInt32("mount_skill_id")
                    };
                    _npcMountSkills.Add(template.Id, template);
                }
            }
        }

        // Mount Skills
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT * FROM mount_skills";
            command.Prepare();
            using (var reader = new SQLiteWrapperReader(command.ExecuteReader()))
            {
                while (reader.Read())
                {
                    var template = new MountSkills
                    {
                        Id = reader.GetUInt32("id"),
                        Name = reader.GetString("name", ""),
                        SkillId = reader.GetUInt32("skill_id")
                    };
                    _mountSkills.Add(template.Id, template);
                }
            }
        }

        // Mount attached skills
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT * FROM mount_attached_skills";
            command.Prepare();
            using (var reader = new SQLiteWrapperReader(command.ExecuteReader()))
            {
                while (reader.Read())
                {
                    var template = new MountAttachedSkills
                    {
                        Id = reader.GetUInt32("id"),
                        MountSkillId = reader.GetUInt32("mount_skill_id"),
                        AttachPointId = (AttachPointKind)reader.GetUInt32("attach_point_id"),
                        SkillId = reader.GetUInt32("skill_id")
                    };
                    _mountAttachedSkills.Add(template.Id, template);
                }
            }
        }

        #endregion MateTables

        #region MateEquipTables

        // Mate equip packs (store/grouping definitions)
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT * FROM mate_equip_packs";
            command.Prepare();
            using (var reader = new SQLiteWrapperReader(command.ExecuteReader()))
            {
                while (reader.Read())
                {
                    _mateEquipPacks.Add(
                        reader.GetUInt32("id"),
                        reader.GetString("name", ""));
                }
            }
        }

        // Which packs are allowed per mate npc template
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT * FROM mate_equip_pack_groups";
            command.Prepare();
            using (var reader = new SQLiteWrapperReader(command.ExecuteReader()))
            {
                while (reader.Read())
                {
                    var npcId = reader.GetUInt32("npc_id");
                    var packId = reader.GetUInt32("mate_equip_pack_id");

                    if (!_mateEquipPackIdsByNpc.TryGetValue(npcId, out var packs))
                    {
                        packs = [];
                        _mateEquipPackIdsByNpc.Add(npcId, packs);
                    }

                    packs.Add(packId);
                }
            }
        }

        // Which items belong to each pack (indexed by item for quick lookup)
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT * FROM mate_equip_pack_items";
            command.Prepare();
            using (var reader = new SQLiteWrapperReader(command.ExecuteReader()))
            {
                while (reader.Read())
                {
                    var packId = reader.GetUInt32("mate_equip_pack_id");
                    var itemId = reader.GetUInt32("item_id");

                    if (!_mateEquipPackIdsByItem.TryGetValue(itemId, out var packs))
                    {
                        packs = [];
                        _mateEquipPackIdsByItem.Add(itemId, packs);
                    }

                    packs.Add(packId);
                }
            }
        }

        // Per mate category allowed equipment slots
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT * FROM mate_equip_slot_packs";
            command.Prepare();
            using (var reader = new SQLiteWrapperReader(command.ExecuteReader()))
            {
                while (reader.Read())
                {
                    var template = new MateEquipSlotPack
                    {
                        Id = reader.GetUInt32("id"),
                        Name = reader.GetString("name", ""),
                        Head = reader.GetBoolean("head", true),
                        Chest = reader.GetBoolean("chest", true),
                        Waist = reader.GetBoolean("waist", true),
                        Feet = reader.GetBoolean("feet", true)
                    };
                    _mateEquipSlotPacks.Add(template.Id, template);
                }
            }
        }

        Logger.Info($"MateGameData: Loaded {_mateEquipPacks.Count} mate equip packs, {_mateEquipPackIdsByNpc.Count} pack groups, {_mateEquipPackIdsByItem.Count} pack items and {_mateEquipSlotPacks.Count} slot packs.");

        #endregion MateEquipTables
    }

    public void PostLoad()
    {
        // Nothing to do here
    }
}
