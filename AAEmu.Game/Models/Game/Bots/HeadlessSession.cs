using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;

namespace AAEmu.Game.Models.Game.Bots;

/// <summary>
/// Internal headless session (M2b / M6.0-lite slice, additive).
///
/// An "internal headless session" is an ordinary Character record created
/// without a network GameConnection. The character is a normal gameplay
/// object: real Inventory, CharacterQuests, abilities, parent world. Packets
/// produced by the quest engine drop at Unit.SendPacket's null-safe
/// `Connection?.SendPacket` sink, so no client is required and no parallel
/// network stack exists.
///
/// Composition rule (AGENTS.md #9/#10): bots compose around ordinary
/// Character records + normal gameplay services. This class only *creates*
/// the record and its world; a PlayerBotController drives it. No bot-only
/// quest state, no direct DB writes, no quest-engine bypass.
/// </summary>
public class HeadlessSession
{
    public Character Character { get; }
    public WorldInstance World { get; }

    /// <summary>
    /// Real network connection backing this session. Null for pure-headless
    /// pilot bots; set (via <see cref="FromNetworkCharacter"/>) when the
    /// character entered the world through the REAL login/enter-world flow —
    /// the M2b-E2E network-session bridge. The connection is the listener's
    /// real GameConnection: no auth bypass, no direct session injection.
    /// </summary>
    public AAEmu.Game.Core.Network.Connections.GameConnection Connection { get; private set; }

    private HeadlessSession(Character character, WorldInstance world)
    {
        Character = character;
        World = world;
    }

    /// <summary>
    /// M2b-E2E: wraps a character that entered the world through the real
    /// network flow (real Login + Game + MySQL) as a bot session. The
    /// character is an ordinary DB-loaded Character with a real
    /// GameConnection; a PlayerBotController drives it through the real quest
    /// engine exactly like the pilot's headless bots.
    /// </summary>
    public static HeadlessSession FromNetworkCharacter(Character character)
    {
        var world = character.ParentWorld as WorldInstance;
        if (world == null)
            throw new InvalidOperationException("Networked bot character has no parent WorldInstance");

        return new HeadlessSession(character, world) { Connection = character.Connection };
    }

    /// <summary>
    /// Creates a fresh headless character (no Connection — packets no-op).
    /// </summary>
    /// <param name="characterId">Character id (DB-record style id; no DB row is created).</param>
    /// <param name="name">Display name.</param>
    /// <param name="level">Starting level (level gates in AddQuest evaluate against it).</param>
    /// <param name="race">Race (race gates, e.g. kind-3 Race=Nuian on the mount chain).</param>
    /// <param name="worldTemplateId">World template id; 0 = default main world.</param>
    public static HeadlessSession Create(uint characterId, string name, byte level,
        Race race = Race.Nuian, uint worldTemplateId = 0)
    {
        var character = new Character(new UnitCustomModelParams())
        {
            Id = characterId,
            Name = name,
            Level = level,
            Race = race,
            Gender = Gender.Male,
            // Ordinary creation-time defaults (normally read from the character
            // row: num_inv_slot / num_bank_slot). Without slots the inventory
            // containers have zero capacity and item acquisition silently drops.
            NumInventorySlots = 50,
            NumBankSlots = 50
        };

        character.Inventory = new Inventory(character);
        character.Appellations = new CharacterAppellations(character);
        character.Abilities = new CharacterAbilities(character);
        // A real player picks three ability trees; seed a defensive spread so
        // reward exp (AddActiveExp -> Abilities lookup) always lands.
        character.Ability1 = AbilityType.Fight;
        character.Ability2 = AbilityType.Magic;
        character.Ability3 = AbilityType.Will;
        character.Quests = new CharacterQuests(character);
        // Every ordinary character row carries a faction (faction_id). Unit
        // requirement gates (MotherFaction/FactionMatch) dereference
        // Faction.MotherId — a null Faction NREs the gate (UnitReqs.cs:199).
        // Nuian -> Nuia Alliance mother (148), matching the DB default for a
        // Nuian character.
        character.Faction = new AAEmu.Game.Models.Game.Faction.SystemFaction
        {
            Id = AAEmu.Game.Models.StaticValues.FactionsEnum.Nuian,
            MotherId = AAEmu.Game.Models.StaticValues.FactionsEnum.NuiaAlliance
        };

        var world = CreateWorld(worldTemplateId);
        SetParentWorld(character, world);

        return new HeadlessSession(character, world);
    }

    /// <summary>
    /// Spawns an NPC into the session world so report / guard / acceptor
    /// lookups (ParentWorld.GetNpc) resolve. Dedupes by template id.
    /// </summary>
    /// <returns>The NPC object id (objId) used by DoReportEvents.</returns>
    public uint SpawnNpc(uint npcTemplateId)
    {
        if (npcTemplateId == 0)
            return 0;

        var existing = World.GetNpcByTemplateId(npcTemplateId);
        if (existing != null)
            return existing.ObjId;

        var npc = new Npc
        {
            ObjId = _nextObjId++,
            TemplateId = npcTemplateId,
            Hp = 100,
            MaxHp = 100
        };
        World.AddObject(npc);
        return npc.ObjId;
    }

    /// <summary>
    /// Spawns a doodad into the session world so report-doodad lookups resolve.
    /// </summary>
    public uint SpawnDoodad(uint doodadTemplateId)
    {
        if (doodadTemplateId == 0)
            return 0;

        var doodad = new Doodad
        {
            ObjId = _nextObjId++,
            TemplateId = doodadTemplateId
        };
        World.AddObject(doodad);
        return doodad.ObjId;
    }

    private uint _nextObjId = 1000;

    private static WorldInstance CreateWorld(uint worldTemplateId)
    {
        // Minimal world container: the quest path only needs NPC/doodad
        // lookups (GetNpc/GetDoodad) and the sphere-quest registry.
        var template = new WorldTemplate
        {
            Id = worldTemplateId,
            Name = "headless_world",
            ZoneKeys = [],
            CellX = 2,
            CellY = 2,
            ZoneKeyByRegions = new uint[32, 32]
        };
        var world = new WorldInstance(template, 0, true, 1);
        world.SphereQuestManager = new SphereQuestManager(world);
        return world;
    }

    private static void SetParentWorld(Character character, WorldInstance world)
    {
        var field = typeof(GameObject).GetField("_parentWorld",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        field?.SetValue(character, world);
    }
}
