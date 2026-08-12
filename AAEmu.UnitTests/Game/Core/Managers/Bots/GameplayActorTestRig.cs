using System.Collections.Concurrent;
using System.Numerics;

using AAEmu.Commons.Network.Core;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;
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
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Quests.Templates;
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

    /// <summary>Object id assigned to the actor character (nonzero, registered in its world).</summary>
    public const uint ActorObjId = 0x1001;

    // Actors get UNIQUE objIds from here — the engine's WorldManager
    // _characters registry is keyed by ObjId and TryAdd is first-wins, so
    // sharing 0x1001 across actors would make UnMountMate's
    // GetCharacterByObjId resolve a stale rider from an earlier test.
    private static uint _nextActorObjId = ActorObjId;

    /// <summary>Next unique actor objId (starts at <see cref="ActorObjId"/>).</summary>
    public static uint NextActorObjId() => _nextActorObjId++;

    private static int _nextWorldInstanceId = 1;

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
            var itemManager = new ItemManager(
                Mock.Of<ISkillManager>().Object,
                Mock.Of<IItemIdManager>().Object,
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

        // Transform.SetParent → Character.SendDebugMessage → CharacterManager.Instance
        // (the mount attach path). DI singleton with no parameterless ctor —
        // seed with mocked deps, missing-only.
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
        // Prod assigns MateManager right after world creation (WorldManager.cs
        // — the CSMountMatePacket path resolves it via ParentWorld.MateManager);
        // the headless world does not, so mirror prod here.
        session.World.MateManager = new MateManager(session.World);
        // Prod fills the region grid right after world creation too
        // (WorldManager.CreateInstance — new Region[CellX*SECTORS_PER_CELL,
        // CellY*SECTORS_PER_CELL], pre-filled). The headless world leaves it
        // null, so the real SetPosition → AddVisibleObject → GetRegionByPos
        // chain (dismount repositions the character) NREs. Missing-only
        // guard: never replace a grid another rig already seeded. ZoneKeys is
        // empty in the headless template, so use 0 like prod's first key.
        if (session.World.Regions == null)
        {
            var dx = session.World.Template.CellX * WorldManager.SECTORS_PER_CELL;
            var dy = session.World.Template.CellY * WorldManager.SECTORS_PER_CELL;
            session.World.Regions = new Region[dx, dy];
            for (var y = 0; y < dy; y++)
            for (var x = 0; x < dx; x++)
                session.World.Regions[x, y] = new Region(session.World, x, y, 0);
        }
        // Prod registers every world instance in WorldManager._worlds at
        // creation (CreateInstance) with a UNIQUE instance id. The headless
        // world ctor hardcodes instanceId 1 for every session, so the real
        // Transform.InstanceId → ParentWorld → GetWorld chain (region
        // AddObject after a SetPosition) would resolve the FIRST session's
        // world for all later actors. Bump the id until free, then register.
        var worlds = (ConcurrentDictionary<uint, WorldInstance>)GetField(WorldManager.Instance, "_worlds");
        while (worlds.ContainsKey(session.World.Id))
        {
            var nextId = (uint)Interlocked.Increment(ref _nextWorldInstanceId);
            typeof(WorldInstance).GetProperty(nameof(WorldInstance.Id))!.SetValue(session.World, nextId);
        }
        worlds.TryAdd(session.World.Id, session.World);
        // Register the character in its world (the production activation path
        // does this; the headless session alone does not).
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
}
