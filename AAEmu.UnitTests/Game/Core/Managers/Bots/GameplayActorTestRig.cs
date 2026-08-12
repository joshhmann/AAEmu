using System.Collections.Concurrent;
using System.Numerics;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.GameData;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Quests.Templates;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Units;
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
            SeedSkillManager();
            FormulaManager.Instance.Load();

            s_seeded = true;
        }
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
        character.ObjId = ActorObjId;
        // Vitals so the real skill path sees an alive caster.
        character.Hp = 100;
        character.MaxHp = 100;
        character.Mp = 100;
        character.MaxMp = 100;
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
}
