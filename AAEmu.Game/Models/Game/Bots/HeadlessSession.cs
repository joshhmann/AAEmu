using AAEmu.Commons.Utils;
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

using NLog;

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
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

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
    ///     banned accounts — no login-server code change). Idempotent: an
    ///     existing bot_managed_* row is reused as-is.
    ///  2. Adopt-or-create the character row:
    ///     - Name NOT registered → ordinary creation shape (CharacterManager
    ///       template spawn position + faction, NameManager reservation,
    ///       CharacterIdManager id), persisted via
    ///       <see cref="Character.SaveDirectlyToDatabase"/> — a real
    ///       characters row owned by the managed account.
    ///     - Name ALREADY registered (a prior boot provisioned it) → ADOPT
    ///       the existing row: reload it from the DB and re-embody. Only rows
    ///       owned by the SAME managed bot account are adopted — the
    ///       NameManager duplicate guard still protects human names
    ///       (squatting), so a restart WITHOUT a DB wipe comes up with the
    ///       same citizens instead of failing with NameAlreadyExists
    ///       (restart-idempotency, t_db5b2be7).
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
    /// <exception cref="ArgumentException">Invalid managed username or character
    /// name, or a character name owned by another (non-bot) account.</exception>
    /// <exception cref="InvalidOperationException">Non-bot account collision, or the character row save failed.</exception>
    /// <param name="appearance">
    /// Optional player-like appearance (P1 t_61814965 — BotAppearanceFactory).
    /// When provided, a FRESH character row is born with the generated model
    /// params + starting equipment, exactly like a human create. Adopted rows
    /// (prior boot) keep their stored look — appearance is baked at birth.
    /// When null, the race-appropriate canonical default is used.
    /// </param>
    public static HeadlessSession Provision(string username, string name, Race race = Race.Nuian,
        Gender gender = Gender.Male, byte level = 1, BotAppearance? appearance = null)
    {
        name = name.NormalizeName();

        // Fail fast on bad input BEFORE any side effects: name rules mirror the
        // human create path (NameManager regex + duplicate check). A bot
        // character name lives in the same namespace as human names.
        // NameAlreadyExists is NOT a failure here — a registered name owned by
        // this bot's managed account is a prior boot's row and is ADOPTED
        // below (restart-idempotency, t_db5b2be7).
        var nameError = NameManager.Instance.ValidateCharacterName(name);
        if (nameError != CharacterCreateError.Ok && nameError != CharacterCreateError.NameAlreadyExists)
            throw new ArgumentException(
                $"Provisioning failed: character name '{name}' rejected by NameManager ({nameError})", nameof(name));

        // 1. Real managed account row (HeadlessBot flag + client-login block).
        var account = BotAccountProvisioningService.Instance.ProvisionBotAccount(username);

        // 2b. ADOPT path — the name is already registered. Reload the existing
        //     row (owned by this bot's managed account) and re-embody it
        //     instead of creating a duplicate.
        if (nameError == CharacterCreateError.NameAlreadyExists)
        {
            var existingId = ResolveAdoptableBotCharacterId(NameManager.Instance, name, account.AccountId);
            var adoptedCharacter = Character.Load(existingId)
                ?? throw new InvalidOperationException(
                    $"Provisioning failed: name '{name}' is registered but characters row {existingId} is missing or deleted");
            if (adoptedCharacter.AccountId != account.AccountId)
                throw new InvalidOperationException(
                    $"Provisioning failed: name '{name}' row {existingId} belongs to account {adoptedCharacter.AccountId} but NameManager maps it to {account.AccountId} (registry/row desync — refusing adoption)");

            // P0 hotfix t_76730833: rows provisioned BEFORE the model-params
            // fix carry a degenerate 1-byte blob (type=None) — the client
            // renders the name tag but no body. Heal the in-memory params
            // AND persist them so the row stays visible across reboots.
            //
            // P0 hotfix t_d0889187 (demo body source): model-10 bots must
            // carry the EXACT Asssaa blob (733 at bytes 2-5) — rows from the
            // hotfix-#4 era have the right structure (231B) but hair 1, so
            // the degenerate check alone won't upgrade them. Detect via the
            // demo-bytes comparison and replace when it doesn't match.
            var adoptedTemplate = CharacterManager.Instance.GetTemplate(adoptedCharacter.Race, adoptedCharacter.Gender);
            var needDemoBlob = adoptedTemplate?.ModelId == 10 && !BotAppearanceDefaults.IsDemoAppearance(adoptedCharacter.ModelParams);
            if (needDemoBlob || BotAppearanceDefaults.IsDegenerate(adoptedCharacter.ModelParams))
            {
                adoptedCharacter.ModelParams = BotAppearanceDefaults.BuildDefault(
                    adoptedCharacter.Race, adoptedCharacter.Gender, adoptedTemplate?.ModelId ?? 0);
                if (!adoptedCharacter.SaveDirectlyToDatabase())
                    Logger.Warn("Provisioning: adopted '{Name}' (id {Id}) — model-params heal save FAILED", name, adoptedCharacter.Id);
                else if (needDemoBlob)
                    Logger.Info("Provisioning: upgraded unit_model_params to demo appearance for adopted bot '{Name}' (id {Id})", name, adoptedCharacter.Id);
                else
                    Logger.Info("Provisioning: healed degenerate unit_model_params for adopted bot '{Name}' (id {Id})", name, adoptedCharacter.Id);
            }

            // M6.6 t_747a1c44: rows provisioned before the parity seeding
            // carry no skills, no actabilities, an all-None action bar and an
            // empty bag (parity audit: 0 skill rows / 0 vs 34 actability /
            // 85B blob vs human 137B). Heal the missing seeding in-memory AND
            // persist it so the row stays a full player across reboots.
            // Seed-if-missing guards (ApplyPlayerProgression /
            // ApplyStarterBagSupplies) make the heal idempotent: a healed row
            // is a no-op on the next boot.
            var healedSeeding = false;
            if (adoptedCharacter.Inventory?.Bag is { Items.Count: 0 })
            {
                CharacterManager.Instance.ApplyStarterBagSupplies(adoptedCharacter);
                healedSeeding = true;
            }

            if (adoptedCharacter.Skills is not { Skills.Count: > 0 } ||
                adoptedCharacter.Actability is not { Actabilities.Count: > 0 } ||
                !adoptedCharacter.Slots.Any(s => s.Type == ActionSlotType.Spell))
            {
                CharacterManager.Instance.ApplyPlayerProgression(adoptedCharacter);
                healedSeeding = true;
            }

            if (healedSeeding && !adoptedCharacter.SaveDirectlyToDatabase())
                Logger.Warn("Provisioning: adopted '{Name}' (id {Id}) — parity-seeding heal save FAILED", name, adoptedCharacter.Id);
            else if (healedSeeding)
                Logger.Info("Provisioning: healed skills/actabilities/bag supplies for adopted bot '{Name}' (id {Id})", name, adoptedCharacter.Id);

            // P0 hotfix t_d0889187: rows provisioned before the equipment fix
            // have NO body-part items — the 1.2 client builds the character
            // mesh from the equipment section, so a bot with an empty
            // equipment container renders tags + positions but no body.
            // Heal: equip the template body parts (slots 19-25) when the
            // container has none. Items persist via the periodic SaveManager
            // tick (Character.Save deliberately skips Inventory.Save — see
            // M2b-E2E restart persistence notes).
            var botHasBodyParts = false;
            for (var s = (int)EquipmentItemSlot.Face; s <= (int)EquipmentItemSlot.Beard && !botHasBodyParts; s++)
                botHasBodyParts = adoptedCharacter.Inventory?.Equipment?.GetItemBySlot(s) != null;
            if (!botHasBodyParts && adoptedTemplate != null)
            {
                var equipped = BotAppearanceDefaults.EquipTemplateBodyParts(adoptedCharacter, adoptedTemplate);
                if (equipped > 0)
                    Logger.Info("Provisioning: healed missing body-part equipment for adopted bot '{Name}' (id {Id}) — equipped {Equipped} body parts",
                        name, adoptedCharacter.Id, equipped);
                else
                    Logger.Warn("Provisioning: adopted bot '{Name}' (id {Id}) has no body parts and the heal equipped none", name, adoptedCharacter.Id);
            }
            Logger.Info("Provisioning: adopted existing character row '{Name}' (id {Id}, account {AccountId}) — re-embodying",
                name, adoptedCharacter.Id, account.AccountId);

            CharacterLifecycleService.Instance.ActivateHeadless(adoptedCharacter, new BotContext
            {
                BotId = adoptedCharacter.Id,
                Name = name
            });

            var adoptedWorld = adoptedCharacter.ParentWorld as WorldInstance;
            if (adoptedWorld == null)
                throw new InvalidOperationException("Provisioning failed: activated bot character has no parent WorldInstance");

            return new HeadlessSession(adoptedCharacter, adoptedWorld) { ProvisionedAccount = account };
        }

        // 2. Real character row — ordinary creation shape, persisted for real.
        var template = CharacterManager.Instance.GetTemplate(race, gender)
            ?? throw new InvalidOperationException($"Provisioning failed: no character template for race {race} / gender {gender} (server data not loaded?)");

        var characterId = CharacterIdManager.Instance.GetNextId();
        // Register the name BEFORE the row save, exactly like the human create
        // path (CharacterManager.Create): bot names occupy the same namespace
        // as human names, so duplicates and human-side squatting are rejected
        // by the same registry. RemoveCharacterId on failure releases it.
        NameManager.Instance.AddCharacter(characterId, name, account.AccountId);
        var character = BuildProvisionedCharacter(characterId, account.AccountId, name, race, gender, level, template, appearance);

        // P1 t_61814965: a fresh citizen is born with its player-like
        // starting equipment (per-class gear pack + race body items + newbie
        // consumables) — the same data the human create path applies.
        if (appearance != null)
            CharacterManager.Instance.ApplyStartingEquipment(character, appearance);

        // M6.6 t_747a1c44: a fresh citizen is born with the human create-path
        // progression — ability-1 tree, full actability set, start-ability
        // skill rows + action-bar spell slots. SaveDirectlyToDatabase below
        // persists the skills/actabilities rows exactly like a human create.
        CharacterManager.Instance.ApplyPlayerProgression(character);

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
    /// PRODUCTION convenience overload (P1 t_61814965): generates a full
    /// player-like appearance from the spec via
    /// <see cref="BotAppearanceFactory"/> — randomized-but-valid model
    /// params (type=Face), race/gender-canonical model id, per-class
    /// starting equipment, name from the race pool — then provisions the bot
    /// with it. Deterministic per seed: the same spec yields the same name
    /// and the same born look.
    /// </summary>
    /// <param name="username">Managed bot account name (bot_managed_* namespace).</param>
    /// <param name="spec">Appearance spec (race/gender required; class, seed, name optional).</param>
    /// <param name="level">Starting level.</param>
    public static HeadlessSession Provision(string username, BotAppearanceSpec spec, byte level = 1)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var appearance = BotAppearanceFactory.Instance.Generate(spec);
        return Provision(username, appearance.Name, spec.Race, spec.Gender, level, appearance);
    }

    /// <summary>
    /// Resolves whether a registered character name can be ADOPTED by a bot
    /// provisioning call (restart-idempotency, t_db5b2be7). Returns the
    /// existing character id when the name is registered AND owned by the
    /// given bot account — a prior boot's row. Throws when the name is
    /// registered under ANY other account: bots never adopt foreign rows
    /// (human squatting protection — the NameManager duplicate guard stays in
    /// force for human names). Returns 0 for unregistered names (fresh create
    /// path).
    /// </summary>
    internal static uint ResolveAdoptableBotCharacterId(INameManager nameManager, string normalizedName, uint botAccountId)
    {
        var existingId = nameManager.GetCharacterId(normalizedName);
        if (existingId == 0)
            return 0;

        var owningAccountId = nameManager.GetCharacterAccount(existingId);
        if (owningAccountId != botAccountId)
            throw new ArgumentException(
                $"Provisioning failed: character name '{normalizedName}' already exists and is owned by account {owningAccountId} — bots only adopt rows owned by the same managed bot account (squatting protection)",
                nameof(normalizedName));

        return existingId;
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
        Race race, Gender gender, byte level, CharacterTemplate template, BotAppearance? appearance = null)
    {
        // P0 hotfix t_76730833: a bare UnitCustomModelParams serializes a
        // 1-byte blob (type=None) and the 1.2 client cannot build the
        // character mesh from empty custom model params — name tags render,
        // the body does not (prod evidence: Citizen01-03 rows = 0x00 vs a
        // real human row's 231-byte Face blob). Provision with a full Face
        // blob: the factory-generated appearance when one is given (P1
        // t_61814965), else the race-appropriate canonical default.
        var modelParams = appearance?.ModelParams
            ?? BotAppearanceDefaults.BuildDefault(race, gender, template.ModelId);

        var character = new Character(modelParams)
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
            Ability1 = appearance?.ClassAbility ?? AbilityType.Fight,
            Ability2 = AbilityType.Magic,
            Ability3 = AbilityType.Will,
            Created = DateTime.UtcNow,
            ReturnDistrictId = template.ReturnDistrictId,
            ResurrectionDistrictId = template.ResurrectionDistrictId,
            // Bots never receive a client spawn packet, so
            // CSSpawnCharacterPacket's VisualOptions assignment never runs for
            // them — carry an ordinary default instance instead of null (P0
            // hotfix t_506a9acb: null VisualOptions NRE'd SCUnitStatePacket
            // while serializing bots to a real client).
            VisualOptions = new CharacterVisualOptions()
        };

        character.Transform.ApplyWorldSpawnPosition(template.SpawnPosition);

        character.Slots = new ActionSlot[Character.MaxActionSlots];
        for (var i = 0; i < character.Slots.Length; i++)
            character.Slots[i] = new ActionSlot();

        character.Inventory = new Inventory(character);
        // P0 hotfix t_d0889187 (final visibility layer): the 1.2 client
        // builds the character mesh from the EQUIPMENT section of
        // SCUnitStatePacket — body-part items (face/hair/body, slots 19-25)
        // carry the mesh asset ids; without them the client renders tags and
        // positions but no body (NPC path guards validFlags<=0 as "no body
        // and no face"). Mirror the human create path: equip the template
        // body parts exactly like CharacterManager.Create does.
        BotAppearanceDefaults.EquipTemplateBodyParts(character, template);
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
        // E2E-fixture path — same ordinary default as Provision's
        // BuildProvisionedCharacter (no client spawn packet ever sets this).
        character.VisualOptions = new CharacterVisualOptions();
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
