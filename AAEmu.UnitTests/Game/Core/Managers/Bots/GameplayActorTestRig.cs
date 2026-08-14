using System.Collections.Concurrent;
using System.Numerics;

using AAEmu.Commons.Network.Core;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.Stream;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Managers.Stream;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.GameData;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Auction;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.CommonFarm;
using AAEmu.Game.Models.Game.CommonFarm.Static;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Funcs;
using AAEmu.Game.Models.Game.DoodadObj.Static;
using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.Housing;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Game.Items.Containers;
using AAEmu.Game.Models.Game.Items.Loots;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.Housing;
using AAEmu.Game.Models.Game.CommonFarm.Static;
using AAEmu.Game.Models.Game.Mails;
using AAEmu.Game.Models.Game.Merchant;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Quests.Templates;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Effects;
using AAEmu.Game.Models.Game.Skills.Effects.Enums;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Slaves;
using AAEmu.Game.Models.Game.Team;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.World.Transform;
using AAEmu.Game.Models.Game.World.Zones;
using AAEmu.Game.Utils;
using AAEmu.UnitTests.Utils.Mocks;
using AAEmu.UnitTests.Game.Housing;

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
    /// Seeds missing singletons + the minimal skill template. The singleton
    /// surface is one-shot (s_seeded); the idempotent dict/template healing
    /// (SeedSkillManager + SeedItemManager) runs on EVERY call so actor
    /// tests re-heal the shared managers after a sibling rig swaps them
    /// (t_277eaa57: PlayerbotPilotRig.SeedPilotSingletons →
    /// QuestScenarioDriver.SeedSingletons UNCONDITIONALLY replaces
    /// SkillManager/ItemManager with fresh instances whose dictionaries are
    /// null/empty; the one-shot guard alone would leave every later actor
    /// test dereferencing null _skills / empty _templates — the combined
    /// GameplayActor run's order dependence). Must run before any actor is
    /// created. Safe in any suite ordering.
    /// </summary>
    public static void Seed()
    {
        lock (typeof(GameplayActorTestRig))
        {
            if (!s_seeded)
            {
                SeedBaseSurface();
                EnsureIncrementingItemIds();
                SeedTradeSurface();
                FormulaManager.Instance.Load();

                s_seeded = true;
            }

            // Idempotent + additive (missing-only per dict/template), so
            // re-running after a sibling singleton swap is safe and never
            // clobbers established data (t_4f11a519 discipline).
            SeedSkillManager();
            SeedItemManager();
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

    internal static bool SingletonSeeded(Type singletonBase)
        => singletonBase.GetField("s_instance", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            ?.GetValue(null) != null;

    internal static void SeedSingleton(Type singletonBase, object instance)
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
        // BuyBackItems is created by Character.Load() (line 2615); the
        // headless fixture path never runs Load, and the M5.1 Sell engine
        // path (CSSellItemsPacket branch) moves the sold item into it.
        character.BuyBackItems = new ItemContainer(character.Id, SlotType.None, false, character);
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

    // ------------------------------------------------------------------ vehicle drive rig

    /// <summary>Default test ground vehicle (Slave) objId for the DriveVehicle rig.</summary>
    public const uint SlaveObjId = 0x3001;

    /// <summary>
    /// Summons a test ground vehicle (Slave): a real Slave object registered
    /// in the session world (object + base-unit + slave registries), owned by
    /// the actor, at the given position. Mirrors the M4 integrated-session
    /// rig's group-cart shape (mountable SlaveTemplate).
    /// </summary>
    public static uint SummonSlave(HeadlessSession session, GameplayActor actor, uint slaveObjId = SlaveObjId, Vector3 position = default)
    {
        var slave = new Slave
        {
            ObjId = slaveObjId,
            TlId = (ushort)(slaveObjId & 0xFFFF),
            Id = slaveObjId,
            Name = "test-cart",
            Template = new SlaveTemplate
            {
                Id = 15,
                Name = "test-cart",
                ModelId = 129,
                Mountable = true,
                SlaveKind = SlaveKind.Boat,
                PortalTime = 0f,
                Level = 1
            },
            Hp = 1000,
            Mp = 100,
            Summoner = actor.Character
        };
        slave.Transform.Local.SetPosition(position);
        // Same instance-id bypass as CreateActor: the headless world is not
        // in the shared WorldManager registry, so the public InstanceId
        // setter would NRE — pre-set the backing fields instead (the
        // _parentWorld backing field avoids the InstanceId side effect of
        // the ParentWorld setter).
        typeof(AAEmu.Game.Models.Game.World.Transform.Transform)
            .GetField("_instanceId", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(slave.Transform, session.World.Id);
        typeof(AAEmu.Game.Models.Game.World.GameObject)
            .GetField("_parentWorld", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(slave, session.World);
        session.World.AddObject(slave);
        return slave.ObjId;
    }

    /// <summary>
    /// Puts the actor into the DRIVER seat of a test slave through the REAL
    /// engine path — SlaveManager.BindSlave, the exact call CSBindSlavePacket
    /// and the AttachTo effect use (driver-lock + seat-occupied checks run).
    /// The slave must exist in the session world first (SummonSlave).
    /// </summary>
    public static void BindSlaveDriver(HeadlessSession session, GameplayActor actor, uint slaveObjId)
        => session.World.SlaveManager.BindSlave(actor.Character, slaveObjId, AttachPointKind.Driver, AttachUnitReason.NewMaster);

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
    /// Seeds the trade-pack surface for the LoadPackOntoVehicle tests
    /// (t_a7756a00) — a DEDICATED pack template + put-down skill + doodad
    /// template, isolated from the M5.1 pack surface (PackTemplateId /
    /// PlacedPackDoodadTemplateId) so registering the placed-pack doodad
    /// template in the DoodadManager cannot change PackPickup/PutDown
    /// behavior (those tests rely on Create() returning null there). Also
    /// seeds the slave cargo surface (SlaveGameData attach points).
    /// </summary>
    public static void SeedCargoPackSurface()
    {
        Seed();
        SeedCargoPackItemTemplate();
        SeedCargoPutDownSkill();
        SeedCargoPackDoodadTemplate();
        SeedSlaveCargoSurface();
    }

    /// <summary>Dedicated trade-pack template for the cargo-load tests.</summary>
    public const uint CargoPackTemplateId = 264_901;

    /// <summary>Put-down use skill of <see cref="CargoPackTemplateId"/>.</summary>
    public const uint CargoPutDownSkillId = 290_901;

    /// <summary>Placed-pack doodad template of <see cref="CargoPackTemplateId"/> — REGISTERED
    /// in the DoodadManager so the carried-load path can spawn it through the real factory.</summary>
    public const uint CargoPackDoodadTemplateId = 290_902;

    /// <summary>Slave template id of the rig cargo vehicle.</summary>
    public const uint CargoSlaveTemplateId = 290_100;

    /// <summary>Model id of the rig cargo vehicle (Farm Wagon shape — canonical 1.2
    /// Farm Wagon model 1008 attach-point data is seeded under this id).</summary>
    public const uint CargoSlaveModelId = 290_101;

    /// <summary>Canonical pack-storage-box doodad (1.2: "등짐 보관 상자", model
    /// interaction.xml/container.empty) — the cargo-point marker of slave_doodad_bindings.</summary>
    public const uint CanonicalPackStorageBoxDoodadId = 3446;

    private static void SeedCargoPackItemTemplate()
    {
        var templates = (Dictionary<uint, ItemTemplate>)GetField(ItemManager.Instance, "_templates");
        if (!templates.TryGetValue(CargoPackTemplateId, out var template))
        {
            template = new BackpackTemplate { Id = CargoPackTemplateId, Name = "Test Cargo Trade Pack" };
            templates[CargoPackTemplateId] = template;
        }

        template.MaxCount = 1;
        template.FixedGrade = 0;
        template.Gradable = false;
        template.UseSkillId = CargoPutDownSkillId;
        ((BackpackTemplate)template).BackpackType = BackpackType.TradePack;
    }

    private static void SeedCargoPutDownSkill()
    {
        var manager = SkillManager.Instance;
        var skills = (Dictionary<uint, SkillTemplate>)GetField(manager, "_skills");
        if (!skills.TryGetValue(CargoPutDownSkillId, out var template))
        {
            template = new SkillTemplate
            {
                Id = CargoPutDownSkillId,
                ManaCost = 0,
                CastingTime = 0,
                CooldownTime = 0,
                MinRange = 0,
                MaxRange = 100,
                TargetType = AAEmu.Game.Models.Game.Skills.SkillTargetType.Self,
                TargetSelection = AAEmu.Game.Models.Game.Skills.SkillTargetSelection.Target
            };
            skills[CargoPutDownSkillId] = template;
        }

        var effect = new PutDownBackpackEffect
        {
            Id = 290_903,
            BackpackDoodadId = CargoPackDoodadTemplateId
        };
        template.Effects =
        [
            new SkillEffect
            {
                EffectId = effect.Id,
                Template = effect,
                StartLevel = 1,
                EndLevel = 55,
                Chance = 10_000,
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

        effectDict[effect.Id] = effect;
    }

    /// <summary>
    /// Registers the cargo pack's placed-pack doodad template in the
    /// DoodardManager so DoodadManager.Create can materialize it (the
    /// carried-load path spawns the pack through the real factory).
    /// Missing-only; a minimal template (no func groups → GetFuncGroupId
    /// returns 0 → InitDoodad has no phase funcs to run).
    /// </summary>
    private static void SeedCargoPackDoodadTemplate()
    {
        SeedDoodadManager();
        var templates = (Dictionary<uint, DoodadTemplate>)GetField(DoodadManager.Instance, "_templates");
        if (!templates.ContainsKey(CargoPackDoodadTemplateId))
            templates[CargoPackDoodadTemplateId] = new DoodadTemplate { Id = CargoPackDoodadTemplateId };
    }

    /// <summary>
    /// Seeds the SlaveGameData singleton (missing-only) with the canonical
    /// 1.2 Farm Wagon (model 1008) attach points under the rig model id —
    /// the exact slave_attach_points.json values (points 9-12, the cart
    /// cargo points). SlaveManager.ApplyAttachPointLocation reads this map
    /// to snap loaded packs onto the cargo point (retail snap behavior).
    /// </summary>
    public static void SeedSlaveCargoSurface()
    {
        if (!SingletonSeeded(typeof(Singleton<SlaveGameData>)))
        {
            var gameData = new SlaveGameData();
            SetField(gameData, "_attachPoints",
                new Dictionary<uint, Dictionary<AttachPointKind, WorldSpawnPosition>>());
            SeedSingleton(typeof(Singleton<SlaveGameData>), gameData);
        }

        var attachPoints = (Dictionary<uint, Dictionary<AttachPointKind, WorldSpawnPosition>>?)
            GetField(SlaveGameData.Instance, "_attachPoints");
        if (attachPoints == null)
        {
            attachPoints = [];
            SetField(SlaveGameData.Instance, "_attachPoints", attachPoints);
        }

        if (!attachPoints.ContainsKey(CargoSlaveModelId))
        {
            attachPoints[CargoSlaveModelId] = new Dictionary<AttachPointKind, WorldSpawnPosition>
            {
                [AttachPointKind.Cannon0] = new WorldSpawnPosition { X = -0.55f, Y = -2.0f, Z = 1.15f },
                [AttachPointKind.Cannon1] = new WorldSpawnPosition { X = 0.55f, Y = -2.0f, Z = 1.15f },
                [AttachPointKind.Cannon2] = new WorldSpawnPosition { X = 0.55f, Y = -3.15f, Z = 1.15f },
                [AttachPointKind.Cannon3] = new WorldSpawnPosition { X = -0.55f, Y = -3.15f, Z = 1.15f },
            };
        }
    }

    /// <summary>
    /// Summons the rig cargo vehicle (Farm Wagon shape): a real Slave with
    /// a template carrying the canonical pack-storage-box bindings (cargo
    /// points 9-12), registered in the session world. The slave spawns at
    /// the actor's position so range checks pass.
    /// </summary>
    public static Slave SummonCargoSlave(HeadlessSession session, GameplayActor actor,
        uint slaveObjId, int cargoPoints = 4)
    {
        var template = new SlaveTemplate
        {
            Id = CargoSlaveTemplateId,
            Name = "test-farm-wagon",
            ModelId = CargoSlaveModelId,
            Mountable = true,
            SlaveKind = SlaveKind.Machine,
            Level = 1,
        };
        for (var i = 0; i < cargoPoints; i++)
        {
            template.DoodadBindings.Add(new SlaveDoodadBindings
            {
                Id = CargoSlaveTemplateId * 100 + (uint)i,
                OwnerId = CargoSlaveTemplateId,
                OwnerType = "Slave",
                AttachPointId = (AttachPointKind)((int)AttachPointKind.Cannon0 + i), // 9..12 = cart cargo points
                DoodadId = CanonicalPackStorageBoxDoodadId,
                Persist = false,
                Scale = 1f,
            });
        }

        var slave = new Slave
        {
            ObjId = slaveObjId,
            TlId = (ushort)(slaveObjId & 0xFFFF),
            Id = slaveObjId,
            Name = "test-farm-wagon",
            Template = template,
            ModelId = CargoSlaveModelId,
            Hp = 1000,
            Mp = 100,
            Summoner = actor.Character,
        };
        var pos = actor.Character.Transform.World.Position;
        slave.Transform.Local.SetPosition(pos);
        // Headless registry bypass (the PlacePackDoodad / CreateActor
        // pattern): the ParentWorld setter re-enters
        // Transform.InstanceId → WorldManager.GetWorld, and the headless
        // world is not in the shared registry. Set the backing fields
        // directly; Region.AddObject's InstanceId assignment then no-ops.
        typeof(AAEmu.Game.Models.Game.World.GameObject)
            .GetField("_parentWorld", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(slave, session.World);
        typeof(AAEmu.Game.Models.Game.World.Transform.Transform)
            .GetField("_instanceId", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(slave.Transform, session.World.Id);
        session.World.AddObject(slave);
        return slave;
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

    // ------------------------------------------------------- M5.1 trade rig

    /// <summary>Merchant pack id used by the Buy/Sell merchant rig.</summary>
    public const uint MerchantPackId = 88_001;

    /// <summary>Item template the Buy rig's merchant sells (Price set).</summary>
    public const uint BuyItemTemplateId = 88_101;

    /// <summary>Item template the Sell rig stocks (Refund + Sellable set).</summary>
    public const uint SellItemTemplateId = 88_102;

    /// <summary>Item template the auction rig lists (Refund + Sellable set).</summary>
    public const uint AuctionItemTemplateId = 88_103;

    /// <summary>
    /// Seeds the M5.1 trade singleton surface (missing-only): NpcManager
    /// (merchant goods), AuctionManager (auction lots + ids), MailManager
    /// (the auction purchase path mails through the engine's own Send),
    /// and the ItemManager grade registry (the sell refund formula
    /// dereferences GetGradeTemplate). Safe in any suite ordering; never
    /// replaces an established singleton.
    /// </summary>
    public static void SeedTradeSurface()
    {
        SeedNpcManager();
        SeedAuctionManager();
        SeedMailManager();
        SeedGrades();
        SeedTradeItemTemplates();
    }

    private static void SeedNpcManager()
    {
        if (!SingletonSeeded(typeof(Singleton<NpcManager>)))
        {
            var npcManager = new NpcManager(
                Mock.Of<IObjectIdManager>().Object,
                Mock.Of<IModelManager>().Object,
                Mock.Of<IFactionManager>().Object,
                ItemManager.Instance,
                Mock.Of<IAIManager>().Object);
            SeedSingleton(typeof(Singleton<NpcManager>), npcManager);
        }

        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        var goodsField = typeof(NpcManager).GetField("Goods", flags)
                         ?? typeof(NpcManager).GetField("<Goods>k__BackingField", flags);
        if (goodsField?.GetValue(NpcManager.Instance) == null)
            goodsField?.SetValue(NpcManager.Instance, new Dictionary<uint, MerchantGoods>());
    }

    private static void SeedAuctionManager()
    {
        if (!SingletonSeeded(typeof(Singleton<AuctionManager>)))
        {
            var auctionIdMock = Mock.Of<IAuctionIdManager>();
            var nextLotId = 100u;
            auctionIdMock.GetNextId().Returns(() => nextLotId++);
            var auctionManager = new AuctionManager(
                ItemManager.Instance,
                Mock.Of<INameManager>().Object,
                auctionIdMock.Object,
                Mock.Of<ILocalizationManager>().Object,
                Mock.Of<ITaskManager>().Object);
            SeedSingleton(typeof(Singleton<AuctionManager>), auctionManager);
        }
    }

    /// <summary>
    /// Seeds MailManager so the auction purchase path's engine mail delivery
    /// (MailManager.Instance.Send) does not throw in tests: the engine's
    /// Send verifies the receiver through its own INameManager — the mock
    /// fails the name/id verification and returns false cleanly (no mail is
    /// stored, no DB touched). The auction state change (money deducted,
    /// lot removed) happens BEFORE the mail attempt in the engine path.
    /// </summary>
    private static void SeedMailManager()
    {
        if (SingletonSeeded(typeof(Singleton<MailManager>)))
            return;
        var nameMock = Mock.Of<INameManager>();
        nameMock.GetCharacterName(Arg.Any<uint>()).Returns((string)null);
        nameMock.GetCharacterId(Arg.Any<string>()).Returns(1u); // != 0 → id verification fails → Send returns false
        var mailManager = new MailManager(
            Mock.Of<IMailIdManager>().Object,
            nameMock.Object,
            ItemManager.Instance,
            Mock.Of<ITaskManager>().Object,
            Mock.Of<IWorldManager>().Object,
            new Lazy<IHousingManager>(() => Mock.Of<IHousingManager>().Object),
            Mock.Of<ILocalizationManager>().Object);
        // The engine's Send stores into _allPlayerMails only after name
        // verification passes — with the failing mock it never does, but the
        // field is also only initialized by Load(); seed the empty dict so
        // any path that reaches it cannot NRE.
        typeof(MailManager).GetField("_allPlayerMails",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            ?.SetValue(mailManager, new Dictionary<long, BaseMail>());
        SeedSingleton(typeof(Singleton<MailManager>), mailManager);
    }

    /// <summary>Seeds the ItemManager grade registry (grade 0 → 100% refund).</summary>
    private static void SeedGrades()
    {
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        var gradesField = typeof(ItemManager).GetField("_grades", flags);
        if (gradesField?.GetValue(ItemManager.Instance) == null)
        {
            gradesField?.SetValue(ItemManager.Instance, new Dictionary<int, GradeTemplate>
            {
                [0] = new GradeTemplate { Grade = 0, RefundMultiplier = 100 }
            });
        }
    }

    /// <summary>
    /// Seeds the trade item templates (Buy/Sell/Auction). Property updates
    /// are ALWAYS applied (idempotent) so a sibling rig's bare seed cannot
    /// shadow the Price/Refund/Sellable values this surface needs.
    /// </summary>
    private static void SeedTradeItemTemplates()
    {
        SeedTradeItemTemplate(BuyItemTemplateId, price: 50, refund: 0, sellable: false);
        SeedTradeItemTemplate(SellItemTemplateId, price: 0, refund: 25, sellable: true);
        SeedTradeItemTemplate(AuctionItemTemplateId, price: 0, refund: 25, sellable: true);
    }

    /// <summary>
    /// Registers (or updates) an item template with explicit trade
    /// properties: Price (merchant buy cost), Refund (merchant sell payout
    /// before grade multiplier), Sellable (merchant sell gate).
    /// </summary>
    public static void SeedTradeItemTemplate(uint templateId, int price, int refund, bool sellable)
    {
        var templates = (Dictionary<uint, ItemTemplate>)GetField(ItemManager.Instance, "_templates");
        if (!templates.TryGetValue(templateId, out var template))
        {
            template = new ItemTemplate { Id = templateId, MaxCount = 100, FixedGrade = -1 };
            templates[templateId] = template;
        }

        template.Price = price;
        template.Refund = refund;
        template.Sellable = sellable;
    }

    /// <summary>
    /// Registers a merchant goods pack (NpcManager.Goods) that sells the
    /// given item template ids, and returns the pack id.
    /// </summary>
    public static uint SeedMerchantPack(params uint[] itemTemplateIds)
    {
        SeedNpcManager();
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        var goodsField = typeof(NpcManager).GetField("Goods", flags)
                         ?? typeof(NpcManager).GetField("<Goods>k__BackingField", flags)
                         ?? throw new InvalidOperationException("Cannot locate NpcManager.Goods backing field");
        var goods = (Dictionary<uint, MerchantGoods>)goodsField.GetValue(NpcManager.Instance)!;
        if (!goods.TryGetValue(MerchantPackId, out var pack))
        {
            pack = new MerchantGoods(MerchantPackId);
            goods[MerchantPackId] = pack;
        }

        foreach (var templateId in itemTemplateIds)
            pack.AddItemToStock(templateId, 0);
        return MerchantPackId;
    }

    /// <summary>
    /// Spawns a merchant NPC (Template.Merchant + MerchantPackId set — the
    /// gates both the Buy and Sell engine paths check) at the actor's
    /// position. Returns the NPC objId.
    /// </summary>
    public static uint SpawnMerchantNpc(HeadlessSession session, uint npcTemplateId = 1000, uint packId = MerchantPackId)
    {
        var objId = session.SpawnNpc(npcTemplateId);
        var npc = session.World.GetNpc(objId);
        if (npc != null)
        {
            npc.Template = new NpcTemplate
            {
                Id = npcTemplateId,
                Merchant = true,
                MerchantPackId = packId
            };
        }

        return objId;
    }

    /// <summary>Sets the actor's money balance (ordinary Character.Money).</summary>
    public static void SetMoney(GameplayActor actor, long amount)
        => actor.Character.Money = amount;

    /// <summary>Sets an NPC's world position (shop-range tests).</summary>
    public static void SetNpcPosition(HeadlessSession session, uint npcObjId, System.Numerics.Vector3 position)
    {
        var npc = session.World.GetNpc(npcObjId);
        if (npc != null)
            npc.Transform.Local.SetPosition(position);
    }

    /// <summary>
    /// Seeds an auction lot directly into AuctionManager.AuctionLots with
    /// the given terms (the purchase path resolves lots from that registry).
    /// The lot's item is created through the REAL ItemManager.Create so the
    /// engine's post-sale item lookup (GetItemByItemId) resolves it.
    /// </summary>
    public static ulong SeedAuctionLot(uint lotId, uint itemTemplateId, int count, int startPrice, int buyoutPrice,
        uint clientId, string clientName, AuctionDuration duration = AuctionDuration.AuctionDuration6Hours)
    {
        SeedAuctionManager();
        var item = ItemManager.Instance.Create(itemTemplateId, count, 0);
        var lot = new AuctionLot
        {
            Id = lotId,
            Duration = duration,
            Item = item,
            EndTime = DateTime.UtcNow.AddHours(6),
            WorldId = 1,
            ClientId = clientId,
            ClientName = clientName,
            StartMoney = startPrice,
            DirectMoney = buyoutPrice,
            PostDate = DateTime.UtcNow,
            BidWorldId = 255,
            BidderId = 0,
            BidderName = "",
            BidMoney = 0,
            Extra = 0,
            IsDirty = true
        };
        AuctionManager.Instance.AuctionLots[lotId] = lot;
        return lotId;
    }

    // ------------------------------------------------------------------ M5.1 plant rig

    /// <summary>Plantable seed item template (mapped to <see cref="TestCropDoodadId"/>).</summary>
    public const uint TestSeedItemId = 93_001;

    /// <summary>Seed whose doodad mapping points at an UNSEEDED template (GetTemplate returns null).</summary>
    public const uint TestUnseededSeedItemId = 93_002;

    /// <summary>Use skill of the plant seeds (ConsumeLaborPower = <see cref="TestPlantLaborCost"/>).</summary>
    public const uint TestPlantSkillId = 93_101;

    /// <summary>Growing-crop doodad template the seeds map to.</summary>
    public const uint TestCropDoodadId = 93_201;

    /// <summary>Mapped doodad id for <see cref="TestUnseededSeedItemId"/> — deliberately NOT in DoodadManager._templates.</summary>
    public const uint TestUnseededDoodadId = 93_202;

    /// <summary>Public-farm subzone key (farm_type 1 = Farm).</summary>
    public const uint TestFarmSubZoneId = 93_301;

    /// <summary>ObjId the rigged DoodadManager hands out (constant — one planted doodad per test world).</summary>
    public const uint TestDoodadObjId = 0x200000;

    /// <summary>Labor the plant use skill consumes (mirrors canonical seeds like potato 15659 → skill 25536).</summary>
    public const int TestPlantLaborCost = 5;

    /// <summary>
    /// Seeds the M5.1 plant surface (additive, missing-only — never replaces
    /// an established seed): the seed item templates, the plant use skill
    /// (labor 5, no effects/reagents so the engine consumes exactly one seed
    /// via CreatePlayerDoodad's ConsumeItem), the item_spawn_doodads mapping,
    /// the growing-crop doodad template, and the PublicFarm/Housing/
    /// CommonFarmGameData singletons the CSCreateDoodadPacket gates
    /// dereference. Also initializes DoodadIdManager (missing-only) so the
    /// engine's Doodad.Save() reaches its MySQL write — plant tests point
    /// MySQL at a dead port (M3b convention) so that write fails fast and
    /// deterministically.
    /// </summary>
    public static void SeedPlantSurface()
    {
        Seed();
        SeedPlantItemTemplates();
        SeedPlantSkill();
        SeedPlantDoodadSurface();
        SeedPlantFarmSurface();
        SeedNameManager();
    }

    private static void SeedPlantItemTemplates()
    {
        var templates = (Dictionary<uint, ItemTemplate>)GetField(ItemManager.Instance, "_templates");
        foreach (var templateId in new[] { TestSeedItemId, TestUnseededSeedItemId })
        {
            if (!templates.TryGetValue(templateId, out var template))
            {
                template = new ItemTemplate { Id = templateId, Name = $"Test Seed {templateId}" };
                templates[templateId] = template;
            }
            // Properties are ALWAYS applied (a sibling bare seed must not
            // shadow the plant shape).
            template.MaxCount = 100; // stackable — the engine stores template-only item refs
            template.FixedGrade = -1;
            template.UseSkillId = TestPlantSkillId;
        }
    }

    private static void SeedPlantSkill()
    {
        var skills = (Dictionary<uint, SkillTemplate>)GetField(SkillManager.Instance, "_skills");
        if (!skills.TryGetValue(TestPlantSkillId, out var template))
        {
            template = new SkillTemplate
            {
                Id = TestPlantSkillId,
                ManaCost = 0,
                CastingTime = 0,
                CooldownTime = 0,
                MinRange = 0,
                MaxRange = 100,
                TargetType = AAEmu.Game.Models.Game.Skills.SkillTargetType.Self,
                TargetSelection = AAEmu.Game.Models.Game.Skills.SkillTargetSelection.Target,
                ConsumeLaborPower = TestPlantLaborCost
            };
            skills[TestPlantSkillId] = template;
        }
        else
        {
            template.ConsumeLaborPower = TestPlantLaborCost;
        }
        // No Effects and no reagent mapping: the seed's use-skill pipeline
        // must NOT consume anything by itself — CreatePlayerDoodad's
        // ConsumeItem is the single consumption point under test.
    }

    private static void SeedPlantDoodadSurface()
    {
        SeedDoodadManager();
        var templates = (Dictionary<uint, DoodadTemplate>)GetField(DoodadManager.Instance, "_templates");
        if (!templates.ContainsKey(TestCropDoodadId))
            templates[TestCropDoodadId] = new DoodadTemplate { Id = TestCropDoodadId };

        var itemDoodad = (Dictionary<uint, ItemDoodadTemplate>?)GetField(ItemManager.Instance, "_itemDoodadTemplates");
        if (itemDoodad == null)
        {
            itemDoodad = [];
            SetField(ItemManager.Instance, "_itemDoodadTemplates", itemDoodad);
        }
        if (!itemDoodad.ContainsKey(TestCropDoodadId))
            itemDoodad[TestCropDoodadId] = new ItemDoodadTemplate { DoodadId = TestCropDoodadId, ItemIds = [TestSeedItemId] };
        if (!itemDoodad.ContainsKey(TestUnseededDoodadId))
            itemDoodad[TestUnseededDoodadId] = new ItemDoodadTemplate { DoodadId = TestUnseededDoodadId, ItemIds = [TestUnseededSeedItemId] };

        // Doodad.Save() allocates the row id via DoodadIdManager BEFORE the
        // MySQL write; the plant tests point MySQL at a dead port so the
        // write fails fast, and the id manager must be initialized to reach
        // it (missing-only, t_6bad0654 discipline).
        var freeIdsField = typeof(IdManager).GetField("_freeIds",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (freeIdsField?.GetValue(DoodadIdManager.Instance) == null)
            DoodadIdManager.Instance.Initialize(false);
    }

    /// <summary>
    /// Seeds the singleton surfaces the CSCreateDoodadPacket mirrors touch:
    /// PublicFarmManager, HousingManager (empty registry — no house at the
    /// placement position), CommonFarmGameData (empty farm-group tables).
    /// Missing-only per singleton, with null-dict hardening for the case
    /// where a Singleton auto-created the instance before this seed ran.
    /// The PublicFarmManager's subzone probe is ALWAYS rewired to a
    /// flag-driven mock (same field-swap pattern as
    /// <see cref="EnsureIncrementingItemIds"/>): with the gate off it
    /// reports "not a farm" — behaviorally identical to whatever mock was
    /// seeded — and <see cref="SetFarmGateEnabled"/> flips it per test.
    /// </summary>
    private static void SeedPlantFarmSurface()
    {
        if (!SingletonSeeded(typeof(Singleton<PublicFarmManager>)))
        {
            var farmManager = new PublicFarmManager(
                Mock.Of<ITaskManager>().Object, Mock.Of<IWorldManager>().Object, Mock.Of<ISubZoneManager>().Object);
            SeedSingleton(typeof(Singleton<PublicFarmManager>), farmManager);
        }
        var subZone = Mock.Of<ISubZoneManager>();
        subZone.GetSubZoneByPosition(Any<WorldTemplate>(), Any<Vector3>()).Returns(() =>
            s_farmGateEnabled ? new List<uint> { TestFarmSubZoneId } : []);
        SetField(PublicFarmManager.Instance, "<subZoneManager>P", subZone);
        if ((Dictionary<uint, FarmType>?)GetField(PublicFarmManager.Instance, "_farmZones") == null)
            SetField(PublicFarmManager.Instance, "_farmZones", new Dictionary<uint, FarmType>());

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
            SeedSingleton(typeof(Singleton<HousingManager>), manager);
        }
        if ((Dictionary<uint, House>?)GetField(HousingManager.Instance, "_houses") == null)
            SetField(HousingManager.Instance, "_houses", new Dictionary<uint, House>());

        var farmData = CommonFarmGameData.Instance;
        if ((Dictionary<uint, FarmGroup>?)GetField(farmData, "_farmGroup") == null)
            SetField(farmData, "_farmGroup", new Dictionary<uint, FarmGroup>());
        if ((Dictionary<uint, FarmGroupDoodads>?)GetField(farmData, "_farmGroupDoodads") == null)
            SetField(farmData, "_farmGroupDoodads", new Dictionary<uint, FarmGroupDoodads>());
        if ((Dictionary<uint, DoodadGroups>?)GetField(farmData, "_doodadGroups") == null)
            SetField(farmData, "_doodadGroups", new Dictionary<uint, DoodadGroups>());
    }

    private static bool s_farmGateEnabled;

    /// <summary>
    /// Flips the public-farm gate for the plant tests: enabled reports
    /// <see cref="TestFarmSubZoneId"/> (FarmType.Farm) at every position,
    /// disabled reports no subzone at all. Restore to false in TearDown —
    /// the flag is read by the shared subzone mock every probe.
    /// </summary>
    public static void SetFarmGateEnabled(bool enabled)
    {
        s_farmGateEnabled = enabled;
        var farmZones = (Dictionary<uint, FarmType>)GetField(PublicFarmManager.Instance, "_farmZones");
        if (enabled)
            farmZones[TestFarmSubZoneId] = FarmType.Farm;
        else
            farmZones.Remove(TestFarmSubZoneId);
    }

    /// <summary>
    /// Adds/removes <see cref="TestCropDoodadId"/> on the CommonFarmGameData
    /// Farm allowlist (the table CanPlace's GetAllowedDoodads reads).
    /// </summary>
    public static void SetFarmAllowlist(bool allowed)
    {
        var data = CommonFarmGameData.Instance;
        var doodads = (Dictionary<uint, FarmGroupDoodads>)GetField(data, "_farmGroupDoodads");
        if (allowed)
        {
            doodads[1] = new FarmGroupDoodads
            {
                Id = 1,
                FarmGroupId = FarmType.Farm,
                DoodadId = TestCropDoodadId,
                ItemId = TestSeedItemId
            };
        }
        else
        {
            doodads.Remove(1);
        }
    }

    /// <summary>
    /// Seeds NameManager (parameterless ctor — all deps optional) so the
    /// house-permission model's GetCharacterAccount lookup resolves instead
    /// of hitting an unseeded singleton.
    /// </summary>
    private static void SeedNameManager()
    {
        if (!SingletonSeeded(typeof(Singleton<NameManager>)))
            SeedSingleton(typeof(Singleton<NameManager>), new NameManager());
    }

    // ------------------------------------------------------------------ M5.2 housing rig

    /// <summary>Canonical 1.2 '아담한 누이아 주택' design id (cat 1, garden 7.5, 3 build steps × 1 action).</summary>
    public const uint TestHouseDesignId = 172;

    /// <summary>Test house design item template (Build checks presence + ownership; the engine consumes it).</summary>
    public const uint TestDesignItemTemplateId = 93_501;

    /// <summary>Canonical w_solzreed_1 zone key (faction 148 NuiaAlliance; group 1 allows category 1).</summary>
    public const uint TestHouseZoneKey = 9;

    /// <summary>
    /// Seeds the M5.2 house-build surface (additive, missing-only):
    /// FeaturesManager (Initialize → Fsets — the Build tax branch reads
    /// taxItem), the HousingManager singleton with INCREMENTING id
    /// managers (a mock GetNextId would give every house ObjId/TlId/Id 0
    /// and collapse _housesTl on the 0 key), and the design item template.
    /// The canonical HousingGameData + zone wiring are per-test
    /// (save/restore) — see GameplayActorHouseBuildActionsTests.
    /// </summary>
    public static void SeedHouseBuildSurface()
    {
        Seed();
        if (!SingletonSeeded(typeof(Singleton<FeaturesManager>)))
        {
            var features = new FeaturesManager(Mock.Of<IExperienceManager>().Object);
            features.Initialize();
            // Gold tax path for the M5.2 surface — the engine's own
            // documented toggle (FeaturesManager.cs: "Use gold instead of
            // tax certificates to pay house tax"). The Build pre-flight and
            // the engine branch read the same bit, so the tests exercise
            // the money branch end to end.
            global::AAEmu.Game.Models.Game.Features.Feature taxItem = global::AAEmu.Game.Models.Game.Features.Feature.taxItem;
            FeaturesManager.Fsets.Set(taxItem, false);
            SeedSingleton(typeof(Singleton<FeaturesManager>), features);
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
            SeedSingleton(typeof(Singleton<HousingManager>), manager);
        }

        // Incrementing id managers (FakeObjectIdManager pattern) — the
        // engine allocates house ObjId/TlId/Id from these on every Build.
        SetHousingManagerField("objectIdManager", new FakeObjectIdManager(0xB000));
        SetHousingManagerField("housingIdManager", new FakeIdManager(0xB100));
        SetHousingManagerField("housingTldManager", new FakeIdManager(0xB200));
        // The engine resolves the placement zone through its injected
        // worldManager (GetZoneId over the world template's
        // ZoneKeyByRegions grid) — a Mock<IWorldManager> would return 0
        // and every placement would fail the zone gate, so the real
        // WorldManager.Instance is wired in (same rule as the zone fake).
        SetHousingManagerField("worldManager", WorldManager.Instance);
        // House is a Unit: Region.AddObject writes Transform.ZoneId, whose
        // setter fires Unit.OnZoneChange → ZoneManager.Instance — the
        // concrete singleton is never seeded headless (production seeds it
        // at boot). An unloaded instance is enough: GetZoneByKey returns
        // null, both zone-group ids read 0, and OnZoneChange early-returns
        // before touching buffs (M5.1 doodads never hit this — Doodad is
        // not a Unit).
        if (!SingletonSeeded(typeof(Singleton<ZoneManager>)))
        {
            var zoneManager = new ZoneManager(WorldManager.Instance);
            // Unloaded singleton: _zones/_groups stay null and
            // GetZoneByKey NREs — seed empty dicts so zone lookups return
            // null, both OnZoneChange zone-group ids read 0, and the
            // method early-returns (loading real zones WITHOUT groups
            // would NRE deeper in OnZoneChange's buff branch).
            // _climateElem must also be seeded: GetClimatesByZone /
            // DoodadHasMatchingClimate iterate it (doodad growth paths),
            // and once this singleton is seeded the SingletonSeeded guard
            // blocks other rigs (e.g. CropHarvestLoopRig) from repairing it.
            SetField(zoneManager, "_zones", new Dictionary<uint, Zone>());
            SetField(zoneManager, "_groups", new Dictionary<uint, ZoneGroup>());
            SetField(zoneManager, "_climateElem", new Dictionary<uint, ZoneClimateElem>());
            SeedSingleton(typeof(Singleton<ZoneManager>), zoneManager);
        }
        if (GetField(HousingManager.Instance, "_houses") is not Dictionary<uint, House>)
            SetField(HousingManager.Instance, "_houses", new Dictionary<uint, House>());
        if (GetField(HousingManager.Instance, "_housesTl") is not Dictionary<ushort, House>)
            SetField(HousingManager.Instance, "_housesTl", new Dictionary<ushort, House>());

        // The design item template (Build resolves it for the consume; the
        // canonical item_housings mapping is NOT consulted — the actor
        // takes the design id explicitly, exactly like the packet).
        // MaxCount 100 / FixedGrade -1 mirror the plant-seed templates:
        // AcquireDefaultItem computes free space from template.MaxCount
        // (0 would refuse every stock).
        var templates = (Dictionary<uint, ItemTemplate>)GetField(ItemManager.Instance, "_templates");
        if (!templates.TryGetValue(TestDesignItemTemplateId, out var designTemplate))
        {
            designTemplate = new ItemTemplate { Id = TestDesignItemTemplateId };
            templates[TestDesignItemTemplateId] = designTemplate;
        }
        designTemplate.Name = "Test House Design";
        designTemplate.MaxCount = 100;
        designTemplate.FixedGrade = -1;

        SeedNameManager();
    }

    /// <summary>
    /// Wires the house-build zone path for a session: fills the world
    /// template's ZoneKeyByRegions so WorldManager.GetZoneId resolves the
    /// test position to the given canonical zone key, and points the
    /// HousingManager's zone manager at a fake resolving that key to the
    /// given Zone record (Build reads zone?.Name for the canonical
    /// land-zone join and zone?.FactionId for the faction gate).
    /// </summary>
    public static void WireHouseZone(HeadlessSession session, uint zoneKey, Zone zone)
    {
        var template = session.World.Template;
        var grid = template.ZoneKeyByRegions;
        if (grid == null)
            grid = new uint[template.CellX * WorldManager.SECTORS_PER_CELL, template.CellY * WorldManager.SECTORS_PER_CELL];
        for (var y = 0; y < grid.GetLength(1); y++)
        for (var x = 0; x < grid.GetLength(0); x++)
            grid[x, y] = zoneKey;
        template.ZoneKeyByRegions = grid;
        SetHousingManagerField("zoneManager", new FakeZoneManager(zoneKey, zone));
    }

    private static void SetHousingManagerField(string name, object value)
    {
        var target = HousingManager.Instance;
        var field = target.GetType().GetField($"<{name}>P",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?? target.GetType().GetField(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (field == null)
            throw new InvalidOperationException($"Cannot locate HousingManager field '{name}'");
        field.SetValue(target, value);
    }
}

/// <summary>
/// Incrementing id manager for the housing rig (FakeObjectIdManager
/// pattern). Implements the housing marker interfaces too — the
/// HousingManager ctor parameters are typed IHousingIdManager /
/// IHousingTldManager (both `: IIdManager`).
/// </summary>
public sealed class FakeIdManager : IIdManager, IHousingIdManager, IHousingTldManager
{
    private uint _next;

    public FakeIdManager(uint start = 0xA000) => _next = start;

    public void Load() { }
    public bool Initialize(bool forceReset = false) => true;
    public uint GetNextId() => _next++;
    public uint[] GetNextId(int count) => Enumerable.Range(0, count).Select(_ => _next++).ToArray();
    public void ReleaseId(uint usedObjectId) { }
    public void ReleaseId(IEnumerable<uint> usedObjectIds) { }
}

/// <summary>
/// Zone-key → Zone fake for the housing rig. Build's zone path only
/// resolves GetZoneByKey (zone?.Name / zone?.FactionId); everything else
/// returns inert defaults so the manager never throws headless.
/// </summary>
public sealed class FakeZoneManager : IZoneManager
{
    private readonly uint _zoneKey;
    private readonly Zone _zone;

    public FakeZoneManager(uint zoneKey, Zone zone)
    {
        _zoneKey = zoneKey;
        _zone = zone;
    }

    public Zone GetZoneByKey(uint zoneKey) => zoneKey == _zoneKey ? _zone : null;
    public Zone GetZoneById(uint zoneId) => zoneId == _zone.Id ? _zone : null;
    public ZoneConflict[] GetConflicts() => [];
    public ZoneGroup GetZoneGroupById(uint zoneId) => null;
    public List<uint> GetZoneKeysInZoneGroupById(uint zoneGroupId) => [];
    public uint GetTargetIdByZoneId(uint zoneId) => 0;
    public Vector2 GetZoneOriginCell(uint zoneId) => default;
    public Vector3 ConvertToWorldCoordinates(uint zoneId, Vector3 point) => point;
    public bool DoodadHasMatchingClimate(Doodad doodad) => true;
    public List<Climate> GetClimatesByZone(Zone zone) => [];
    public void Load() { }
}
