using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Char.Templates;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.StaticValues;

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
/// Two creation paths:
///
///  * <see cref="Create"/> — M2b-E2E fixture ONLY (DB-row-less, synthetic
///    world). The ARCHITECTURE_REVIEW correction (b) forbids this as the
///    production citizen path.
///  * <see cref="Provision"/> — PRODUCTION path (review slice 4): real
///    managed bot account row (aaemu_login.users, account_type=HeadlessBot,
///    client login blocked) + real characters row, embodied through
///    ICharacterLifecycleService.ActivateHeadless.
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
    /// The managed account row backing this session (production
    /// <see cref="Provision"/> path only; null for the E2E fixture).
    /// </summary>
    public BotProvisionedAccount ProvisionedAccount { get; private set; }

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
    /// PRODUCTION bot provisioning (ARCHITECTURE_REVIEW slice 4): real managed
    /// account + real character rows, embodied through
    /// <see cref="ICharacterLifecycleService.ActivateHeadless"/>. This is the
    /// production citizen path — <see cref="Create"/> stays E2E-fixture only
    /// (review correction (b)).
    ///
    /// Steps (all real rows, no synthetic state):
    ///  1. <see cref="BotAccountProvisioningService.ProvisionBotAccount"/> —
    ///     aaemu_login.users row with account_type=HeadlessBot + banned=1
    ///     (client login blocked; the login server's existing auth path denies
    ///     banned accounts — no login-server code change).
    ///  2. Ordinary character creation shape (CharacterManager template spawn
    ///     position + faction, NameManager reservation, CharacterIdManager id),
    ///     persisted via <see cref="Character.SaveDirectlyToDatabase"/> — a
    ///     real characters row owned by the managed account.
    ///  3. <see cref="ICharacterLifecycleService.ActivateHeadless"/> — the
    ///     shared entry core (Load → ObjId → TryAddCharacter → buffs/HP/MP),
    ///     no client packets.
    ///
    /// The returned session carries the provisioned account
    /// (<see cref="ProvisionedAccount"/>) so callers can record/verify the
    /// managed credential. Normal persistence (SaveManager periodic save +
    /// leave-save via Deactivate) rides the existing lifecycles — no third
    /// save path (review deliverable 1-F / H4 stays additive for playerbot_*
    /// metadata only).
    /// </summary>
    /// <param name="username">Managed bot account name — MUST be in the
    /// bot_managed_* namespace (see <see cref="BotAccountProvisioningService.ManagedUsernamePrefix"/>).</param>
    /// <param name="name">Character display name (also the characters.name row).</param>
    /// <param name="race">Race; the character template's spawn position and
    /// faction come from the booted CharacterManager (real world placement).</param>
    /// <param name="gender">Gender.</param>
    /// <param name="level">Starting level.</param>
    /// <exception cref="ArgumentException">Invalid managed username or character name.</exception>
    /// <exception cref="InvalidOperationException">Non-bot account collision, or the character row save failed.</exception>
    public static HeadlessSession Provision(string username, string name, Race race = Race.Nuian,
        Gender gender = Gender.Male, byte level = 1)
    {
        // Fail fast on bad input BEFORE any side effects: name rules mirror the
        // human create path (NameManager regex + duplicate check). A bot
        // character name lives in the same namespace as human names.
        var nameError = NameManager.Instance.ValidateCharacterName(name);
        if (nameError != CharacterCreateError.Ok)
            throw new ArgumentException(
                $"Provisioning failed: character name '{name}' rejected by NameManager ({nameError})", nameof(name));

        // 1. Real managed account row (HeadlessBot flag + client-login block).
        var account = BotAccountProvisioningService.Instance.ProvisionBotAccount(username);

        // 2. Real character row — ordinary creation shape, persisted for real.
        var template = CharacterManager.Instance.GetTemplate(race, gender)
            ?? throw new InvalidOperationException($"Provisioning failed: no character template for race {race} / gender {gender} (server data not loaded?)");

        var characterId = CharacterIdManager.Instance.GetNextId();
        var character = BuildProvisionedCharacter(characterId, account.AccountId, name, race, gender, level, template);
        if (!character.SaveDirectlyToDatabase())
        {
            NameManager.Instance.RemoveCharacterId(characterId);
            CharacterIdManager.Instance.ReleaseId(characterId);
            throw new InvalidOperationException($"Provisioning failed: characters row save failed for '{name}' (id {characterId})");
        }

        // 3. Embodiment through the shared lifecycle service (headless variant).
        CharacterLifecycleService.Instance.ActivateHeadless(character, new BotContext
        {
            BotId = characterId,
            Name = name
        });

        var world = character.ParentWorld as WorldInstance;
        if (world == null)
            throw new InvalidOperationException("Provisioning failed: activated bot character has no parent WorldInstance");

        return new HeadlessSession(character, world) { ProvisionedAccount = account };
    }

    /// <summary>
    /// Builds the in-memory Character for provisioning, mirroring the ordinary
    /// creation path (CharacterManager.Create) where it matters for the row:
    /// template spawn position (real world placement), template faction,
    /// inventory/bank slot counts, action slots, ability spread, and the
    /// DB-loadable sub-objects (Inventory/Appellations/Abilities/Quests).
    /// The row is written by SaveDirectlyToDatabase; instance Load() inside
    /// ActivateHeadless re-initializes the rest from the database exactly like
    /// a human character.
    /// </summary>
    private static Character BuildProvisionedCharacter(uint characterId, uint accountId, string name,
        Race race, Gender gender, byte level, CharacterTemplate template)
    {
        var character = new Character(new UnitCustomModelParams())
        {
            Id = characterId,
            TemplateId = characterId,
            AccountId = accountId,
            Name = name,
            Race = race,
            Gender = gender,
            Level = level,
            AccessLevel = AppConfiguration.Instance.Account.AccessLevelDefault,
            NumInventorySlots = template.NumInventorySlot,
            NumBankSlots = template.NumBankSlot,
            Faction = FactionManager.Instance.GetFaction(template.FactionId),
            FactionName = string.Empty,
            Ability1 = AbilityType.Fight,
            Ability2 = AbilityType.Magic,
            Ability3 = AbilityType.Will,
            Created = DateTime.UtcNow,
            ReturnDistrictId = template.ReturnDistrictId,
            ResurrectionDistrictId = template.ResurrectionDistrictId
        };

        character.Transform.ApplyWorldSpawnPosition(template.SpawnPosition);

        character.Slots = new ActionSlot[Character.MaxActionSlots];
        for (var i = 0; i < character.Slots.Length; i++)
            character.Slots[i] = new ActionSlot();

        character.Inventory = new Inventory(character);
        character.Appellations = new CharacterAppellations(character);
        character.Abilities = new CharacterAbilities(character);
        character.Quests = new CharacterQuests(character);

        character.Hp = character.MaxHp;
        character.Mp = character.MaxMp;
        return character;
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
