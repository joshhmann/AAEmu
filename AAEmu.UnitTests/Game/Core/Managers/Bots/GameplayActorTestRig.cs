using System.Collections.Concurrent;
using System.Numerics;

using AAEmu.Commons.Network.Core;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Managers.Stream;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.GameData;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Funcs;
using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Game.Items.Containers;
using AAEmu.Game.Models.Game.Items.Loots;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.Housing;
using AAEmu.Game.Models.Game.CommonFarm.Static;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Quests.Templates;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Effects;
using AAEmu.Game.Models.Game.Skills.Effects.Enums;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Team;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Utils;
using AAEmu.UnitTests.Utils.Mocks;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// Rig for the M5 actor contract tests (slice #8): rigs the seams the
/// actor's REAL engine paths touch, WITHOUT owning any singleton surface.
///
/// Ordering discipline (critical — full-suite runs): the suite shares
/// process-wide singletons between the scenario rig (empty mocks, replaced
/// per test) and the pilot rig (real QuestManager + UnitRequirementsGameData
/// from canonical data, one-shot via its s_seeded flag). This rig therefore
/// NEVER calls PlayerbotPilotRig.SeedPilotSingletons() (that would flip the
/// pilot's one-shot flag early, making later pilot probes skip their real
/// reload after a scenario test replaced QuestManager) and NEVER replaces a
/// singleton that is already established. It seeds only what is missing:
///   - the base empty surface only when nothing seeded it yet;
///   - SkillManager / WorldManager mock-backed instances (scenario surface
///     lacks them; the pilot rig's own additions);
///   - CharacterSkills + CharacterActability + world registration per actor
///     (the headless session does not create them);
///   - one minimal SkillTemplate + tag dictionaries in SkillManager so the
///     Cast accept path runs the REAL Character.UseSkill engine path
///     (requirements, gcd, mana, range, effects, cooldowns) headless;
///   - FormulaManager.Load() (idempotent; real formulas from canonical data).
/// </summary>
public static class GameplayActorTestRig
{
    /// <summary>Seeded skill id for the Cast accept-path test.</summary>
    public const uint TestSkillId = 90001;

    /// <summary>
    /// Item-use pipeline seeds (B1 UseItem tests): an ordinary usable item
    /// template whose use skill is a real skill template, plus a reagent
    /// mapping so a successful use consumes one unit through the normal
    /// skill pipeline.
    /// </summary>
    public const uint TestItemTemplateId = 1234;
    public const uint TestItemUseSkillId = 90002;

    /// <summary>Object id assigned to the actor character (nonzero, registered in its world).</summary>
    public const uint ActorObjId = 0x1001;

    // Actors get UNIQUE objIds from here — the engine's WorldManager
    // _characters registry is keyed by ObjId and TryAdd is first-wins, so
    // sharing 0x1001 across actors would make UnMountMate's
    // GetCharacterByObjId resolve a stale rider from an earlier test.
    private static uint _nextActorObjId = ActorObjId;

    /// <summary>Next unique actor objId (starts at <see cref="ActorObjId"/>).</summary>
    public static uint NextActorObjId() => _nextActorObjId++;

    // High base so rig worlds never collide with the small fixed instance
    // ids other test files register (e.g. SpecialtyManagerTests' 77,
    // M3bFurniturePersistenceTests' 7, HousingM3aConstructionTests' 1) —
    // the rig bumps through 1,2,3,… per CreateActor, and once it reached a
    // fixed id before that file ran, the file's TryAdd silently failed and
    // its world was invisible to world-wide sweeps.
    private static int _nextWorldInstanceId = 0x4000_0000;

    /// <summary>Default objId for a rig-summoned test mount (SummonMate).</summary>
    public const uint MateObjId = 0x2001;

    private static bool s_seeded;

    /// <summary>
    /// Seeds missing singletons + the minimal skill template. Idempotent;
    /// must run before any actor is created. Safe in any suite ordering.
    /// </summary>
    public static void Seed()
    {
        lock (typeof(GameplayActorTestRig))
        {
            if (s_seeded)
                return;

            SeedBaseSurface();
            EnsureIncrementingItemIds();
            SeedSkillManager();
            SeedItemManager();
            FormulaManager.Instance.Load();

            s_seeded = true;
        }
    }

    /// <summary>
    /// Swaps the ItemManager's item-id source to an incrementing mock when
    /// the current one hands out id 0 for everything (the base surface's
    /// unconfigured mock — every Create would collide on _allItems[0] and
    /// return null, so loot/item grants would silently never land). Missing-
    /// only guard: a real or already-incrementing id source is never replaced
    /// (M3a-3 t_4f0091b8 discipline). Primary-ctor params are captured under
    /// `&lt;paramName&gt;P` fields (C# 12).
    /// </summary>
    private static void EnsureIncrementingItemIds()
    {
        const System.Reflection.BindingFlags flags =
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        var field = typeof(ItemManager).GetField("<itemIdManager>P", flags)
                    ?? typeof(ItemManager).GetField("itemIdManager", flags);
        if (field == null)
            return;

        var current = (IItemIdManager?)field.GetValue(ItemManager.Instance);
        if (current == null || current.GetNextId() != 0)
            return; // a real id source is already in place — leave it

        var mock = Mock.Of<IItemIdManager>();
        var nextItemId = 0x01000000u;
        mock.GetNextId().Returns(() => nextItemId++);
        field.SetValue(ItemManager.Instance, mock.Object);
    }

    /// <summary>
    /// Establishes the base singleton surface with PER-SINGLETON guards —
    /// each is seeded only when missing, never replaced. This is what makes
    /// the rig safe in any full-suite ordering:
    ///  - scenario tests (QuestScenarioTests/TierTests) seed the empty
    ///    driver surface per test — nothing for this rig to redo;
    ///  - pilot probes seed the REAL QuestManager/UnitRequirementsGameData
    ///    one-shot via PlayerbotPilotRig.s_seeded — a real QuestManager must
    ///    never be clobbered by an empty mock, or probes that run later skip
    ///    their reload and see empty tables;
    ///  - QuestNoStartClusterTests seeds QuestManager alone — ItemManager
    ///    and friends must still be seeded for the Character ctor;
    ///  - SkillManager/WorldManager are DI singletons with no parameterless
    ///    ctor and are absent from the scenario surface.
    /// </summary>
    private static void SeedBaseSurface()
    {
        if (!SingletonSeeded(typeof(Singleton<QuestManager>)))
        {
            var questManager = new QuestManager(Mock.Of<ITaskManager>().Object, Mock.Of<IZoneManager>().Object);
            SetField(questManager, "_componentTemplates", new Dictionary<uint, QuestComponentTemplate>());
            SetField(questManager, "_groupItems", new Dictionary<uint, List<uint>>());
            SetField(questManager, "_groupNpcs", new Dictionary<uint, List<uint>>());
            SeedSingleton(typeof(Singleton<QuestManager>), questManager);
        }

        if (!SingletonSeeded(typeof(Singleton<ItemManager>)))
        {
            // Item ids MUST be unique and incrementing: ItemManager.Create
            // registers items in _allItems by id, and the B1 UseItem path
            // resolves the caster item through that registry. A default
            // mock returns 0 for every item — the second created item would
            // collide with the first and silently fail to stock. Use the
            // real id manager (same pattern as QuestIdManager /
            // ContainerIdManager below).
            ItemIdManager.Instance.Initialize(true);
            var itemManager = new ItemManager(
                Mock.Of<ISkillManager>().Object,
                ItemIdManager.Instance,
                Mock.Of<IContainerIdManager>().Object,
                Mock.Of<ILocalizationManager>().Object,
                Mock.Of<ITaskManager>().Object,
                Mock.Of<IWorldManager>().Object);
            SetField(itemManager, "_templates", new Dictionary<uint, ItemTemplate>());
            SetField(itemManager, "_removedItems", new List<ulong>());
            SetField(itemManager, "_allItems", new ConcurrentDictionary<ulong, Item>());
            SeedSingleton(typeof(Singleton<ItemManager>), itemManager);
        }

        if (!SingletonSeeded(typeof(Singleton<UnitRequirementsGameData>)))
        {
            var unitRequirements = new UnitRequirementsGameData();
            var ownerTypes = new[] { "QuestComponent", "Sphere", "Skill", "ItemArmor", "ItemWeapon", "AchievementObjective", "AiEvent" };
            SetField(unitRequirements, "<_unitReqs>k__BackingField", new Dictionary<uint, UnitReqs>());
            SetField(unitRequirements, "<_unitReqsByOwnerType>k__BackingField",
                ownerTypes.ToDictionary(t => t, _ => new List<UnitReqs>()));
            SeedSingleton(typeof(Singleton<UnitRequirementsGameData>), unitRequirements);
        }

        if (!SingletonSeeded(typeof(Singleton<TeamManager>)))
        {
            SeedSingleton(typeof(Singleton<TeamManager>),
                new TeamManager(Mock.Of<IWorldManager>().Object, Mock.Of<IChatManager>().Object, Mock.Of<ITeamIdManager>().Object));
        }

        if (!SingletonSeeded(typeof(Singleton<TaskManager>)))
        {
            var mockTickManager = Mock.Of<ITickManager>();
            mockTickManager.OnTick.Returns(new TickManager.TickEventHandler());
            SeedSingleton(typeof(Singleton<TaskManager>), new TaskManager(mockTickManager.Object));
        }

        if (!SingletonSeeded(typeof(Singleton<ExperienceManager>)))
        {
            var experienceManager = new ExperienceManager();
            var expTemplates = new List<ExperienceLevelTemplate>();
            var expByLevel = new List<int>();
            for (var level = 1; level <= 55; level++)
            {
                expTemplates.Add(new ExperienceLevelTemplate
                {
                    Level = (byte)level,
                    TotalExp = level * 100_000_000,
                    TotalMateExp = level * 100_000_000,
                    SkillPoints = 1
                });
                expByLevel.Add(level * 100_000_000);
            }
            SetField(experienceManager, "_levelTemplatesByLevel", expTemplates);
            SetField(experienceManager, "_expByLevel", expByLevel);
            SetField(experienceManager, "_mateExpByLevel", expByLevel);
            SetField(experienceManager, "<MaxPlayerLevel>k__BackingField", (byte)55);
            SetField(experienceManager, "<MaxMateLevel>k__BackingField", (byte)50);
            SeedSingleton(typeof(Singleton<ExperienceManager>), experienceManager);
        }

        if (!SingletonSeeded(typeof(Singleton<AccountManager>)))
        {
            SeedSingleton(typeof(Singleton<AccountManager>),
                new AccountManager(Mock.Of<ITickManager>().Object, Mock.Of<ITimedRewardsManager>().Object));
        }

        // CharacterManager backs Character.SendDebugMessage (called from
        // Transform.SetParent when the mount pipeline parents the rider's
        // transform). All-mock deps; AccountDetails is a struct so
        // GetEffectiveAccessLevel resolves to (AccessLevel, 0) without a
        // setup.
        if (!SingletonSeeded(typeof(Singleton<CharacterManager>)))
        {
            SeedSingleton(typeof(Singleton<CharacterManager>),
                new CharacterManager(
                    Mock.Of<IWorldManager>().Object,
                    Mock.Of<IAccountManager>().Object,
                    Mock.Of<INameManager>().Object,
                    Mock.Of<ICharacterIdManager>().Object,
                    Mock.Of<IFactionManager>().Object,
                    Mock.Of<ISkillManager>().Object,
                    Mock.Of<IItemManager>().Object,
                    Mock.Of<IHousingManager>().Object,
                    Mock.Of<IFamilyManager>().Object,
                    Mock.Of<IMailManager>().Object,
                    Mock.Of<ITaskManager>().Object));
        }

        // Pilot extras the scenario surface lacks: SkillManager + WorldManager
        // are DI singletons with no parameterless ctor; ContainerIdManager and
        // the persistent-container registry back the ordinary Character ctor.
        SeedSingleton(typeof(Singleton<SkillManager>),
            new SkillManager(Mock.Of<IAnimationManager>().Object, Mock.Of<IPlotManager>().Object));
        SeedSingleton(typeof(Singleton<WorldManager>),
            new WorldManager(
                Mock.Of<ITickManager>().Object,
                Mock.Of<IWorldIdManager>().Object,
                new Lazy<IZoneManager>(() => Mock.Of<IZoneManager>().Object),
                new Lazy<IIndunManager>(() => Mock.Of<IIndunManager>().Object),
                new Lazy<IFamilyManager>(() => Mock.Of<IFamilyManager>().Object)));

        QuestIdManager.Instance.Initialize(true);
        ContainerIdManager.Instance.Initialize(true);
        // The LootingContainer grant path allocates via the REAL ItemIdManager
        // (ItemIdManager.Instance.GetNextId). Initialize only when the free
        // bitset is missing — force-resetting mid-suite would re-issue ids
        // already in use by other rigs (t_6bad0654 hazard class).
        var freeIdsField = typeof(IdManager).GetField("_freeIds",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (freeIdsField?.GetValue(ItemIdManager.Instance) == null)
            ItemIdManager.Instance.Initialize(false);
        var containerField = typeof(ItemManager).GetField("_allPersistentContainers",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        containerField?.SetValue(ItemManager.Instance,
            new ConcurrentDictionary<ulong, AAEmu.Game.Models.Game.Items.Containers.ItemContainer>());
    }

    private static void SetField(object target, string fieldName, object value)
    {
        var field = target.GetType().GetField(fieldName,
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (field == null)
            throw new InvalidOperationException($"Cannot locate field '{fieldName}' on {target.GetType().Name}");
        field.SetValue(target, value);
    }

    private static object GetField(object target, string fieldName)
    {
        var field = target.GetType().GetField(fieldName,
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (field == null)
            throw new InvalidOperationException($"Cannot locate field '{fieldName}' on {target.GetType().Name}");
        return field.GetValue(target)!;
    }

    /// <summary>
    /// Seeds the minimal skill template + the dictionaries the engine paths
    /// the actor touches dereference. The pilot rig's SkillManager is
    /// constructed without Load(), so its dictionaries start null; the
    /// scenario rig may not seed SkillManager at all.
    /// </summary>
    private static void SeedSkillManager()
    {
        var manager = SkillManager.Instance;
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        var skillsField = typeof(SkillManager).GetField("_skills", flags);
        var skills = (Dictionary<uint, SkillTemplate>?)skillsField!.GetValue(manager);
        if (skills == null)
        {
            skills = [];
            skillsField.SetValue(manager, skills);
        }
        foreach (var field in new[] { "_defaultSkills", "_skillReagents", "_skillProducts" })
        {
            var f = typeof(SkillManager).GetField(field, flags);
            if (f!.GetValue(manager) == null)
            {
                var dictType = typeof(Dictionary<,>).MakeGenericType(
                    f.FieldType.GetGenericArguments()[0], f.FieldType.GetGenericArguments()[1]);
                f.SetValue(manager, Activator.CreateInstance(dictType));
            }
        }
        // _commonSkills is a List<uint> (IsCommonSkill enumerates it).
        var commonField = typeof(SkillManager).GetField("_commonSkills", flags);
        if (commonField!.GetValue(manager) == null)
            commonField.SetValue(manager, new List<uint>());
        // Skill tag dictionaries (SkillModifiers → GetSkillTags /
        // GetBuffsByTagId / GetSkillsByTag / GetBuffTags dereference
        // these during ManaCost / range / modifier evaluation).
        foreach (var field in new[] { "_skillTags", "_taggedSkills", "_buffTags", "_taggedBuffs" })
        {
            var f = typeof(SkillManager).GetField(field, flags);
            if (f!.GetValue(manager) == null)
            {
                var dictType = typeof(Dictionary<,>).MakeGenericType(
                    f.FieldType.GetGenericArguments()[0], f.FieldType.GetGenericArguments()[1]);
                f.SetValue(manager, Activator.CreateInstance(dictType));
            }
        }
        if (!skills.ContainsKey(TestSkillId))
        {
            skills[TestSkillId] = new SkillTemplate
            {
                Id = TestSkillId,
                ManaCost = 0,
                CastingTime = 0,
                CooldownTime = 0,
                MinRange = 0,
                MaxRange = 100,
                TargetType = AAEmu.Game.Models.Game.Skills.SkillTargetType.Self,
                TargetSelection = AAEmu.Game.Models.Game.Skills.SkillTargetSelection.Target
            };
        }

        // Item-use skill (B1 UseItem tests): a separate skill id so the
        // item reagent mapping never touches the Cast tests' TestSkillId.
        if (!skills.ContainsKey(TestItemUseSkillId))
        {
            skills[TestItemUseSkillId] = new SkillTemplate
            {
                Id = TestItemUseSkillId,
                ManaCost = 0,
                CastingTime = 0,
                CooldownTime = 0,
                MinRange = 0,
                MaxRange = 100,
                TargetType = AAEmu.Game.Models.Game.Skills.SkillTargetType.Self,
                TargetSelection = AAEmu.Game.Models.Game.Skills.SkillTargetSelection.Target
            };
        }

        // Reagent mapping: using the item consumes one unit of the item
        // template through the ordinary skill-pipeline consumption path.
        var reagents = (Dictionary<uint, SkillReagent>?)typeof(SkillManager)
            .GetField("_skillReagents", flags)!.GetValue(manager);
        if (reagents != null && !reagents.ContainsKey(TestItemUseSkillId))
        {
            reagents[TestItemUseSkillId] = new SkillReagent
            {
                SkillId = TestItemUseSkillId,
                ItemId = TestItemTemplateId,
                Amount = 1
            };
        }
    }

    /// <summary>
    /// Seeds the ordinary usable item template the B1 UseItem tests stock
    /// and use (ItemManager._templates — the same registry the real
    /// item-use path resolves). Idempotent; never replaces an existing
    /// template.
    /// </summary>
    private static void SeedItemManager()
    {
        var manager = ItemManager.Instance;
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        var templatesField = typeof(ItemManager).GetField("_templates", flags);
        var templates = (Dictionary<uint, ItemTemplate>?)templatesField!.GetValue(manager);
        if (templates == null)
        {
            templates = [];
            templatesField.SetValue(manager, templates);
        }

        if (!templates.ContainsKey(TestItemTemplateId))
        {
            templates[TestItemTemplateId] = new ItemTemplate
            {
                Id = TestItemTemplateId,
                UseSkillId = TestItemUseSkillId,
                UseSkillAsReagent = false,
                MaxCount = 99,
                FixedGrade = -1
            };
        }
    }

    private static bool SingletonSeeded(Type singletonBase)
        => singletonBase.GetField("s_instance", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            ?.GetValue(null) != null;

    private static void SeedSingleton(Type singletonBase, object instance)
    {
        var field = singletonBase.GetField("s_instance", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
                    ?? throw new InvalidOperationException($"Cannot locate singleton field on {singletonBase.Name}");
        if (field.GetValue(null) == null)
            field.SetValue(null, instance);
    }

    /// <summary>
    /// Builds a headless actor character registered in its session world
    /// (real WorldInstance — ParentWorld.GetUnit resolves), with the runtime
    /// surfaces the actor's engine paths require.
    /// </summary>
    public static (GameplayActor Actor, HeadlessSession Session) CreateActor(string name = "actor-bot")
    {
        Seed();
        var session = HeadlessSession.Create((uint)name.GetHashCode() & 0xFFFF, name, 1);
        var character = session.Character;
        // UNIQUE per actor: the engine's WorldManager._characters registry is
        // keyed by ObjId (first-wins TryAdd), so a fixed 0x1001 would make
        // UnMountMate's GetCharacterByObjId resolve the first actor of the
        // process for every later test.
        character.ObjId = NextActorObjId();
        // Vitals so the real skill path sees an alive caster.
        character.Hp = 100;
        character.MaxHp = 100;
        character.Mp = 100;
        character.MaxMp = 100;
        // Prod world shape: MateManager + SlaveManager are assigned right
        // after world creation (WorldManager.cs:528 area); headless session
        // worlds don't. The mount pipeline (IGameplayActor.Mount/Dismount)
        // resolves through ParentWorld.MateManager.
        session.World.MateManager = new MateManager(session.World);
        session.World.SlaveManager = new SlaveManager(session.World);
        // Prod allocates the world's region grid at creation
        // (WorldManager.cs:565-574); headless session worlds don't.
        // Character.SetPosition → AddVisibleObject → GetRegionByPos
        // dereferences it — the dismount engine path repositions the rider.
        var regionDx = session.World.Template.CellX * WorldManager.SECTORS_PER_CELL;
        var regionDy = session.World.Template.CellY * WorldManager.SECTORS_PER_CELL;
        session.World.Regions = new Region[regionDx, regionDy];
        var zoneKey = session.World.Template.ZoneKeys.Count > 0 ? session.World.Template.ZoneKeys[0] : 0u;
        for (var y = 0; y < regionDy; y++)
        for (var x = 0; x < regionDx; x++)
            session.World.Regions[x, y] = new Region(session.World, x, y, zoneKey);
        // Keep the character's Transform in sync with its world WITHOUT the
        // global WorldManager registry: Region.AddObject sets
        // Transform.InstanceId → the public setter resolves
        // WorldManager.Instance.GetWorld(instanceId) and would NRE when the
        // headless world is not (or no longer) in the shared _worlds
        // registry — concurrent suites register/remove id-1 worlds mid-run.
        // Pre-setting the backing field makes Region.AddObject's assignment
        // a no-op, so the dismount engine path (SetPosition →
        // AddVisibleObject → Region.AddObject) never touches the registry.
        // Same bypass pattern as HeadlessSession.SetParentWorld.
        typeof(AAEmu.Game.Models.Game.World.Transform.Transform)
            .GetField("_instanceId", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(character.Transform, session.World.Id);
        // Register the character in its world (the production activation path
        // does this; the headless session alone does not). WorldInstance
        // AddObject also registers the character with WorldManager.Instance —
        // REFRESH the fixed ActorObjId slot first so a stale character from
        // an earlier test never shadows this one (UnMountMate resolves the
        // rider via WorldManager.Instance.GetCharacterByObjId).
        WorldManager.Instance.TryRemoveCharacter(character.ObjId);
        session.World.AddObject(character);
        // Surfaces the real activation path initializes but the E2E-fixture
        // session does not.
        character.Skills = new CharacterSkills(character);
        character.Actability = new CharacterActability(character);
        // Learn the seeded skill (real engine gate: Character.Skills).
        character.Skills.AddSkill(new SkillTemplate { Id = TestSkillId }, 1, false);
        return (new GameplayActor(character), session);
    }

    /// <summary>Convenience: spawns an NPC in the session world and returns its objId.</summary>
    public static uint SpawnNpc(HeadlessSession session, uint npcTemplateId = 1000)
        => session.SpawnNpc(npcTemplateId);

    /// <summary>
    /// Stocks items through the REAL acquisition path
    /// (ItemContainer.AcquireDefaultItem — the same path the engine uses
    /// for quest supplies and the pilot's StockInventory).
    /// </summary>
    public static void StockItem(HeadlessSession session, uint itemTemplateId, int count, byte grade = 0)
        => session.Character.Inventory.Bag.AcquireDefaultItem(ItemTaskType.QuestSupplyItems, itemTemplateId, count, grade);

    /// <summary>
    /// Summons a test mount: a real Mate object registered in the session
    /// world AND in the world's MateManager active registry — the two
    /// normal lookup surfaces the mount pipeline resolves (by objId for
    /// Mount, by rider for Dismount). The mate is owned by the actor and
    /// carries an empty driver seat, exactly like a freshly summoned mount.
    /// </summary>
    public static uint SummonMate(HeadlessSession session, GameplayActor actor, uint mateObjId = MateObjId, ushort tlId = 1, uint? ownerObjId = null)
    {
        var mate = new Mate
        {
            ObjId = mateObjId,
            TlId = tlId,
            Name = "test-mount",
            OwnerObjId = ownerObjId ?? actor.ActorId,
            Hp = 100,
            MaxHp = 100,
            // Transform.InternalAttachChild reads the parent unit's Scale
            // when the rider's transform is parented (Mate.Scale →
            // Template.Scale); a template-less mate would NRE mid-mount.
            Template = new NpcTemplate { Scale = 1f }
        };
        session.World.AddObject(mate);

        // Register in MateManager._activeMates keyed by the owner character
        // Id — the same dictionary AddActiveMateAndSpawn fills (registration
        // only; the spawn/broadcast half of that method needs a live
        // client session).
        var registry = (Dictionary<uint, List<Mate>>?)typeof(MateManager)
            .GetField("_activeMates", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(session.World.MateManager);
        if (registry == null)
            throw new InvalidOperationException("MateManager._activeMates not found");
        if (!registry.TryGetValue(actor.Character.Id, out var mates))
            registry[actor.Character.Id] = mates = [];
        mates.Add(mate);
        return mate.ObjId;
    }

    /// <summary>Moves the character to a known start position via the ordinary Transform.</summary>
    public static void SetPosition(GameplayActor actor, Vector3 position)
        => actor.Character.Transform.Local.SetPosition(position);

    // ------------------------------------------------------------------ B1 rig

    /// <summary>Test item template used by the UseItem idempotency rig (consumed on use).</summary>
    public const uint UseItemTemplateId = 91_001;
    /// <summary>Use skill of <see cref="UseItemTemplateId"/> (no effects, no reagents — the
    /// use-skill-as-reagent fallback consumes the source item).</summary>
    public const uint UseItemSkillId = 90_002;

    /// <summary>Item template granted by the Interact rig's loot func.</summary>
    public const uint InteractItemTemplateId = 91_002;

    /// <summary>Doodad group id (phase) of the Interact rig doodad.</summary>
    public const uint InteractDoodadGroupId = 99_101;

    /// <summary>Func id of the Interact rig loot func.</summary>
    public const uint InteractLootFuncId = 99_301;

    /// <summary>Item template granted by the Loot rig's seeded corpse.</summary>
    public const uint LootItemTemplateId = 91_003;

    /// <summary>
    /// Seeds an item template into ItemManager. The template is created when
    /// missing and its B1-relevant properties are ALWAYS set (idempotent) —
    /// skipping the update when the template exists would let an earlier
    /// sibling test's bare seed (e.g. loot rig with no use skill) shadow this
    /// test's use-skill seed. Property updates are harmless to the sibling
    /// rigs; template ids are additive.
    /// </summary>
    public static void SeedItemTemplate(uint templateId, uint useSkillId = 0, bool useSkillAsReagent = false)
    {
        var templates = (Dictionary<uint, ItemTemplate>)GetField(ItemManager.Instance, "_templates");
        if (!templates.TryGetValue(templateId, out var template))
        {
            template = new ItemTemplate { Id = templateId, MaxCount = 100 };
            templates[templateId] = template;
        }
        template.UseSkillId = useSkillId;
        template.UseSkillAsReagent = useSkillAsReagent;
    }

    /// <summary>Seeds a minimal skill template (no effects, no reagents, instant, self-range).</summary>
    public static void SeedSkillTemplate(uint skillId)
    {
        var manager = SkillManager.Instance;
        var skills = (Dictionary<uint, SkillTemplate>)GetField(manager, "_skills");
        if (!skills.ContainsKey(skillId))
        {
            skills[skillId] = new SkillTemplate
            {
                Id = skillId,
                ManaCost = 0,
                CastingTime = 0,
                CooldownTime = 0,
                MinRange = 0,
                MaxRange = 100,
                TargetType = AAEmu.Game.Models.Game.Skills.SkillTargetType.Self,
                TargetSelection = AAEmu.Game.Models.Game.Skills.SkillTargetSelection.Target
            };
        }
    }

    /// <summary>Grants items through the REAL acquisition path (Bag.AcquireDefaultItem).</summary>
    public static void GrantItem(GameplayActor actor, uint templateId, int count)
        => actor.Character.Inventory.Bag.AcquireDefaultItem(ItemTaskType.QuestSupplyItems, templateId, count);

    /// <summary>Bag count of a template (absolute; rig names must be unique per test).</summary>
    public static int BagCount(GameplayActor actor, uint templateId)
        => actor.Character.Inventory.GetItemsCount(templateId);

    /// <summary>
    /// Seeds the DoodadManager singleton surface (missing-only — sibling rigs
    /// like CropHarvestLoopTests / LivestockInteractionTests seed it with real
    /// chains; never replace an established instance). Func/phase dictionaries
    /// are ensured to exist so <see cref="SeedDoodadLootInteraction"/> can
    /// populate them.
    /// </summary>
    private static void SeedDoodadManager()
    {
        if (!SingletonSeeded(typeof(Singleton<DoodadManager>)))
        {
            var objectIdManager = Mock.Of<IObjectIdManager>();
            objectIdManager.GetNextId().Returns(0x200000u);
            var housingManager = Mock.Of<IHousingManager>();
            var manager = new DoodadManager(
                objectIdManager.Object,
                Mock.Of<IDoodadIdManager>().Object,
                ItemManager.Instance,
                new Lazy<IHousingManager>(() => housingManager.Object),
                Mock.Of<ISusManager>().Object);
            SeedSingleton(typeof(Singleton<DoodadManager>), manager);
        }

        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        var dictFields = new[]
        {
            "_templates", "_funcsByGroups", "_funcsById", "_funcTemplates", "_phaseFuncs", "_phaseFuncTemplates"
        };
        foreach (var name in dictFields)
        {
            var field = typeof(DoodadManager).GetField(name, flags);
            if (field?.GetValue(DoodadManager.Instance) == null)
            {
                var dictType = typeof(Dictionary<,>).MakeGenericType(
                    field!.FieldType.GetGenericArguments()[0], field.FieldType.GetGenericArguments()[1]);
                field.SetValue(DoodadManager.Instance, Activator.CreateInstance(dictType));
            }
        }
    }

    /// <summary>
    /// Seeds the DoodadManager surface for one skill-less loot interaction:
    /// a func row in the given group whose template is DoodadFuncLootItem
    /// (always rolls, grants exactly 1). Missing-only per dictionary.
    /// </summary>
    public static void SeedDoodadLootInteraction(uint groupId, uint funcId, uint itemTemplateId)
    {
        SeedDoodadManager();
        var manager = DoodadManager.Instance;
        var funcsByGroups = (Dictionary<uint, List<DoodadFunc>>)GetField(manager, "_funcsByGroups");
        var funcsById = (Dictionary<uint, DoodadFunc>)GetField(manager, "_funcsById");
        var funcTemplates = (Dictionary<string, Dictionary<uint, DoodadFuncTemplate>>)GetField(manager, "_funcTemplates");

        var func = new DoodadFunc
        {
            GroupId = groupId,
            FuncId = funcId,
            FuncKey = funcId,
            FuncType = "DoodadFuncLootItem",
            NextPhase = -1, // stays/looted-final; never advances to a seeded phase
            SkillId = 0
        };
        if (!funcsById.ContainsKey(funcId))
            funcsById[funcId] = func;
        if (!funcsByGroups.TryGetValue(groupId, out var group))
        {
            group = [];
            funcsByGroups[groupId] = group;
        }
        if (group.All(f => f.FuncId != funcId))
            group.Add(func);

        if (!funcTemplates.TryGetValue("DoodadFuncLootItem", out var lootTemplates))
        {
            lootTemplates = [];
            funcTemplates["DoodadFuncLootItem"] = lootTemplates;
        }
        if (!lootTemplates.ContainsKey(funcId))
        {
            lootTemplates[funcId] = new DoodadFuncLootItem
            {
                ItemId = itemTemplateId,
                CountMin = 1,
                CountMax = 2, // Random.Next(1, 2) == always exactly 1
                Percent = 10_000, // chance roll [0,10000) <= Percent → always
                RemainTime = 0
            };
        }
    }

    /// <summary>
    /// Spawns an interactable doodad: raw world object on the given group
    /// (phase) with the DoodadManager func surface seeded.
    /// </summary>
    public static uint SpawnInteractableDoodad(HeadlessSession session, uint groupId, uint funcId, uint itemTemplateId)
    {
        SeedDoodadLootInteraction(groupId, funcId, itemTemplateId);
        var doodadObjId = session.SpawnDoodad(groupId); // template id doubles as the group here
        var doodad = session.World.GetDoodad(doodadObjId);
        doodad.FuncGroupId = groupId;
        // DoFunc → HasOnlyGroupKindStart() reads Template.FuncGroups (Doodad.cs:795);
        // an empty list keeps the one-shot loot doodad alive (start-only rule).
        doodad.Template = new DoodadTemplate { Id = groupId, FuncGroups = [] };
        return doodadObjId;
    }

    /// <summary>
    /// Seeds a loot container with N entries through the real container
    /// surface (LootingContainer.Items). TeamLootingRule + LootOwnerType are
    /// normally set by GenerateLoot; seed them directly (FreeForAll, no roll)
    /// so TryTakeLootLocked's looting-rule branch does not NRE.
    /// </summary>
    public static void SeedLootContainer(BaseUnit owner, params (uint TemplateId, int Count)[] entries)
    {
        var container = owner.LootingContainer;
        container.Items.Clear();
        var index = 0;
        foreach (var (templateId, count) in entries)
        {
            var item = ItemManager.Instance.Create(templateId, count, 0);
            container.Items[(ushort)index++] = new LootingContainerItemEntry
            {
                Owner = container,
                ItemIndex = (ushort)(index - 1),
                Item = item
            };
        }

        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        var ruleField = typeof(LootingContainer).GetProperty("TeamLootingRule", flags);
        ruleField?.SetValue(container, new LootingRule { LootMethod = LootingRuleMethod.FreeForAll });
        var typeField = typeof(LootingContainer).GetProperty("LootOwnerType", flags);
        typeField?.SetValue(container, owner is Npc ? LootOwnerType.Npc : LootOwnerType.Doodad);
    }

    /// <summary>
    /// <summary>
    /// Registers a mate with the world's MateManager through the real
    /// registry shape (_activeMates, ownerId → list) without the spawn/sleep
    /// side effects of AddActiveMateAndSpawn. The mate carries a Driver seat
    /// (Mate ctor default) owned by the actor character.
    /// </summary>
    public static Mate SpawnMate(GameplayActor actor, uint mateObjId, uint tlId)
    {
        var mate = new Mate
        {
            ObjId = mateObjId,
            TlId = (ushort)tlId,
            OwnerObjId = actor.Character.ObjId,
            // Transform attach reads mate.Scale → Template.Scale (Mate.cs:29);
            // a null Template NREs the mount attach (InternalAttachChild).
            Template = new NpcTemplate { Id = 1, Scale = 1f }
        };
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        var matesField = typeof(MateManager).GetField("_activeMates", flags)!;
        var mates = (Dictionary<uint, List<Mate>>)matesField.GetValue(actor.Character.ParentWorld.MateManager)!;
        if (!mates.TryGetValue(actor.Character.Id, out var list))
        {
            list = [];
            mates[actor.Character.Id] = list;
        }
        list.Add(mate);
        return mate;
    }

    /// <summary>
    /// Attaches a real GameConnection to the actor character (the M2b-E2E
    /// network-session bridge shape: connection.ActiveChar == character).
    /// The backing ISession is a no-op mock — packets encode and vanish.
    /// </summary>
    public static void AttachConnection(GameplayActor actor)
    {
        var connection = new GameConnection(Mock.Of<ISession>().Object)
        {
            ActiveChar = actor.Character
        };
        actor.Character.Connection = connection;
    }

    // ------------------------------------------------------------------ M5.1 pack rig

    /// <summary>Test trade-pack item template (BackpackTemplate, BackpackType.TradePack).</summary>
    public const uint PackTemplateId = 92_001;

    /// <summary>Put-down use skill of <see cref="PackTemplateId"/> (canonical shape: pack 26488 → skill 20412).</summary>
    public const uint PackPutDownSkillId = 92_101;

    /// <summary>Placed-pack doodad template (canonical shape: pack 26488 → doodad 6068).</summary>
    public const uint PlacedPackDoodadTemplateId = 92_201;

    /// <summary>Func group (phase) of the placed-pack doodad's recover func.</summary>
    public const uint PlacedPackFuncGroupId = 92_301;

    /// <summary>Func id of the recover row in <see cref="PlacedPackFuncGroupId"/>.</summary>
    public const uint PlacedPackRecoverFuncId = 92_401;

    /// <summary>Effect id under which the put-down effect registers in SkillManager._effects.</summary>
    public const uint PlacedPackEffectId = 92_501;

    /// <summary>
    /// Seeds the M5.1 pack surface (missing-only, additive — never replaces
    /// an established seed): the trade-pack item template, the put-down
    /// skill template + its PutDownBackpackEffect, the recoverable
    /// placed-pack doodad funcs, and the PublicFarm/Housing singletons the
    /// put-down effect dereferences (DI singletons with no parameterless
    /// ctor — unseeded access throws).
    /// </summary>
    public static void SeedPackSurface()
    {
        Seed();
        SeedPackItemTemplate();
        SeedPutDownSkill();
        SeedPackManagers();
    }

    /// <summary>
    /// Seeds the trade-pack item template (BackpackTemplate + TradePack so
    /// IsAutoEquipTradePack resolves true, UseSkillId = put-down skill).
    /// Idempotent; properties are ALWAYS applied (a sibling bare seed must
    /// not shadow the pack shape).
    /// </summary>
    public static void SeedPackItemTemplate()
    {
        var templates = (Dictionary<uint, ItemTemplate>)GetField(ItemManager.Instance, "_templates");
        if (!templates.TryGetValue(PackTemplateId, out var template))
        {
            template = new BackpackTemplate { Id = PackTemplateId, Name = "Test Trade Pack" };
            templates[PackTemplateId] = template;
        }
        template.MaxCount = 1;
        template.FixedGrade = 0;
        template.Gradable = false;
        template.UseSkillId = PackPutDownSkillId;
        ((BackpackTemplate)template).BackpackType = BackpackType.TradePack;
    }

    /// <summary>
    /// Seeds the put-down skill template + its PutDownBackpackEffect. The
    /// skill carries ONE effect (ApplicationMethod SourceOnce — the effect
    /// uses only the caster + SkillItem), gated for all levels, always
    /// rolling. Also registers the effect under SkillManager._effects
    /// ("PutDownBackpackEffect") for loader parity.
    /// </summary>
    public static void SeedPutDownSkill()
    {
        var manager = SkillManager.Instance;
        var skills = (Dictionary<uint, SkillTemplate>)GetField(manager, "_skills");
        if (!skills.TryGetValue(PackPutDownSkillId, out var template))
        {
            template = new SkillTemplate
            {
                Id = PackPutDownSkillId,
                ManaCost = 0,
                CastingTime = 0,
                CooldownTime = 0,
                MinRange = 0,
                MaxRange = 100,
                TargetType = AAEmu.Game.Models.Game.Skills.SkillTargetType.Self,
                TargetSelection = AAEmu.Game.Models.Game.Skills.SkillTargetSelection.Target
            };
            skills[PackPutDownSkillId] = template;
        }
        var effect = new PutDownBackpackEffect
        {
            Id = PlacedPackEffectId,
            BackpackDoodadId = PlacedPackDoodadTemplateId
        };
        template.Effects =
        [
            new SkillEffect
            {
                EffectId = PlacedPackEffectId,
                Template = effect,
                StartLevel = 1,
                EndLevel = 55,
                Chance = 10_000, // ≥ 100 → the dice gate never skips
                ApplicationMethod = SkillEffectApplicationMethod.SourceOnce
            }
        ];

        var effects = (Dictionary<string, Dictionary<uint, EffectTemplate>>)GetField(manager, "_effects");
        if (effects == null)
        {
            effects = [];
            typeof(SkillManager).GetField("_effects",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .SetValue(manager, effects);
        }
        if (!effects.TryGetValue("PutDownBackpackEffect", out var effectDict))
        {
            effectDict = [];
            effects["PutDownBackpackEffect"] = effectDict;
        }
        effectDict[PlacedPackEffectId] = effect;
    }

    /// <summary>
    /// Seeds the singleton surfaces PutDownBackpackEffect dereferences:
    /// PublicFarmManager (InPublicFarm → subzone list) and HousingManager
    /// (GetHouseAtLocation). Mock-backed; the farm probe returns an empty
    /// zone list (never a public farm) and the housing registry is empty
    /// (no house at the placement position). Missing-only per singleton.
    /// </summary>
    public static void SeedPackManagers()
    {
        if (!SingletonSeeded(typeof(Singleton<PublicFarmManager>)))
        {
            var subZoneManager = Mock.Of<ISubZoneManager>();
            subZoneManager.GetSubZoneByPosition(Any<WorldTemplate>(), Any<Vector3>()).Returns([]);
            var farmManager = new PublicFarmManager(
                Mock.Of<ITaskManager>().Object, Mock.Of<IWorldManager>().Object, subZoneManager.Object);
            SetField(farmManager, "_farmZones", new Dictionary<uint, FarmType>());
            SeedSingleton(typeof(Singleton<PublicFarmManager>), farmManager);
        }

        if (!SingletonSeeded(typeof(Singleton<HousingManager>)))
        {
            var manager = new HousingManager(
                Mock.Of<IObjectIdManager>().Object,
                Mock.Of<IFactionManager>().Object,
                Mock.Of<ILocalizationManager>().Object,
                Mock.Of<IWorldManager>().Object,
                Mock.Of<ITaskManager>().Object,
                Mock.Of<ISkillManager>().Object,
                Mock.Of<IHousingIdManager>().Object,
                Mock.Of<IHousingTldManager>().Object,
                Mock.Of<IItemManager>().Object,
                Mock.Of<IMailManager>().Object,
                Mock.Of<INameManager>().Object,
                Mock.Of<IZoneManager>().Object,
                Mock.Of<IDoodadManager>().Object,
                Mock.Of<IUccManager>().Object);
            SetField(manager, "_houses", new Dictionary<uint, House>());
            SeedSingleton(typeof(Singleton<HousingManager>), manager);
        }
    }

    /// <summary>
    /// Seeds the DoodadManager func surface for one recoverable placed-pack
    /// doodad: a DoodadFuncRecoverItem row with the generic world recover
    /// skill (11361 — the exact routing rule CSLootOpenBagPacket uses).
    /// Missing-only per dictionary.
    /// </summary>
    public static void SeedRecoverablePackDoodad(uint groupId = PlacedPackFuncGroupId, uint funcId = PlacedPackRecoverFuncId)
    {
        SeedDoodadManager();
        var manager = DoodadManager.Instance;
        var funcsByGroups = (Dictionary<uint, List<DoodadFunc>>)GetField(manager, "_funcsByGroups");
        var funcsById = (Dictionary<uint, DoodadFunc>)GetField(manager, "_funcsById");
        var funcTemplates = (Dictionary<string, Dictionary<uint, DoodadFuncTemplate>>)GetField(manager, "_funcTemplates");

        var func = new DoodadFunc
        {
            GroupId = groupId,
            FuncId = funcId,
            FuncKey = funcId,
            FuncType = "DoodadFuncRecoverItem",
            NextPhase = -1,
            SkillId = GameplayActor.GenericRecoverItemSkillId
        };
        if (!funcsById.ContainsKey(funcId))
            funcsById[funcId] = func;
        if (!funcsByGroups.TryGetValue(groupId, out var group))
        {
            group = [];
            funcsByGroups[groupId] = group;
        }
        if (group.All(f => f.FuncId != funcId))
            group.Add(func);

        if (!funcTemplates.TryGetValue("DoodadFuncRecoverItem", out var recoverTemplates))
        {
            recoverTemplates = [];
            funcTemplates["DoodadFuncRecoverItem"] = recoverTemplates;
        }
        if (!recoverTemplates.ContainsKey(funcId))
            recoverTemplates[funcId] = new DoodadFuncRecoverItem { Id = funcId };
    }

    /// <summary>
    /// Equips a trade pack into the actor's Backpack equipment slot through
    /// the REAL acquisition path (Equipment.AcquireDefaultItem — the same
    /// call pack crafting uses). Requires the equip surface (ItemGameData /
    /// BuffGameData registries) seeded — see SeedEquipSurface in the pack
    /// test class.
    /// </summary>
    public static void EquipPack(GameplayActor actor, uint templateId = PackTemplateId, uint crafterId = 0)
    {
        var equipped = actor.Character.Inventory.Equipment.AcquireDefaultItem(
            ItemTaskType.CraftPickupProduct, templateId, 1, -1, crafterId);
        if (!equipped)
            throw new InvalidOperationException($"EquipPack: AcquireDefaultItem failed for {templateId}");
    }

    /// <summary>
    /// Seeds a placed trade-pack doodad exactly as PutDownBackpackEffect
    /// leaves one: a world-registered doodad on the recover func group with
    /// ItemId/ItemTemplateId pointing at a pack item that lives in the
    /// actor's System container (the engine's anti-dupe invariant). The
    /// doodad is NON-persistent so the headless Delete()/Save() MySQL tails
    /// stay out of unit tests (the persistent row path is the M4 E2E
    /// restart rig's concern). Positions the doodad 1 m in front of the
    /// actor (the canonical placement offset) so range checks pass.
    /// </summary>
    public static uint PlacePackDoodad(HeadlessSession session, GameplayActor actor, Item packItem,
        uint groupId = PlacedPackFuncGroupId, uint funcId = PlacedPackRecoverFuncId)
    {
        SeedRecoverablePackDoodad(groupId, funcId);
        var doodadObjId = session.SpawnDoodad(groupId); // template id doubles as the group here
        var doodad = session.World.GetDoodad(doodadObjId);
        doodad.TemplateId = PlacedPackDoodadTemplateId;
        doodad.FuncGroupId = groupId; // setter populates CurrentFuncs from the seeded DoodadManager
        doodad.ItemId = packItem.Id;
        doodad.ItemTemplateId = packItem.TemplateId;
        doodad.PlantTime = DateTime.UtcNow;
        doodad.OwnerId = actor.Character.Id;
        doodad.OwnerType = DoodadOwnerType.Character;
        var actorPos = actor.Character.Transform.World.Position;
        doodad.Transform.Local.SetPosition(new Vector3(actorPos.X + 1f, actorPos.Y, actorPos.Z));
        // ParentWorld is normally assigned through Transform.InstanceId's
        // setter, which re-enters WorldManager.GetWorld — the headless
        // world is not (or no longer) in the shared registry, so the
        // HeadlessSession pattern reflection-sets the backing field.
        // Without it, the engine's Delete() NREs at ParentWorld.RemoveObject.
        typeof(AAEmu.Game.Models.Game.World.GameObject)
            .GetField("_parentWorld", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(doodad, session.World);
        return doodadObjId;
    }

    /// <summary>
    /// Registers an additional item template in the shared ItemManager
    /// registry (used by tests that need a NON-usable item — no use skill).
    /// Idempotent; never replaces an existing template.
    /// </summary>
    public static void RegisterPlainItemTemplate(uint templateId)
    {
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        var templatesField = typeof(ItemManager).GetField("_templates", flags);
        var templates = (Dictionary<uint, ItemTemplate>?)templatesField!.GetValue(ItemManager.Instance);
        if (templates == null)
        {
            templates = [];
            templatesField.SetValue(ItemManager.Instance, templates);
        }

        if (!templates.ContainsKey(templateId))
        {
            templates[templateId] = new ItemTemplate
            {
                Id = templateId,
                UseSkillId = 0,
                MaxCount = 99,
                FixedGrade = -1
            };
        }
    }
}
