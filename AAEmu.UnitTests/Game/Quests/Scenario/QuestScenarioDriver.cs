using System.Numerics;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text.Json;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.GameData;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Game.Items.Containers;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Quests.Acts;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.Game.Models.Game.Quests.Templates;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;
using AAEmu.UnitTests.Utils.Mocks;

namespace AAEmu.UnitTests.Game.Quests.Scenario;

/// <summary>
/// Quest scenario harness core driver.
///
/// Builds a Quest from manifest template parts (mirroring the
/// QuestActConAcceptNpcKillTests mock rig: mocked ICharacter-style owner + real
/// Character, mocked IQuestManager/ITickManager + real TaskManager, mocked
/// ISkillManager/IExpressTextManager/IWorldManager), seeds the game singletons
/// the quest engine touches (QuestManager, ItemManager, UnitRequirementsGameData,
/// QuestIdManager) with in-memory data, and drives the full lifecycle:
///
///   START    - StartQuest() via the manifest acceptor shape (type + id), then
///              RunCurrentStep() so the Start component's acceptor act must pass
///   PROGRESS - fire synthetic events (kill credit, item gain/use, talk, sphere
///              entry, ...) then RunCurrentStep()
///   READY    - fire ReportNpc/ReportDoodad/ReportJournal then RunCurrentStep()
///   REWARD   - RunCurrentStep() so supply acts apply (item/copper/exp) and the
///              completed-quest flag is set
///   PERSIST  - WriteData() snapshot (taken after every non-terminal stage)
///              fed into ReadData() on a fresh quest from the same template;
///              round-trip must preserve step/acceptor/componentId/objectives
///
/// No production quest act code is modified; no server/MySQL/client boots; no
/// per-test QuestManager.Load (templates come from manifest parts).
/// </summary>
public class QuestScenarioDriver
{
    private const uint FirstSyntheticActId = 900000;

    /// <summary>Incrementing id manager so every item created across a multi-quest run
    /// gets a unique instance id (a flat mock returning 0 collides in the live registry).</summary>
    private sealed class IncrementingIdManager : AAEmu.Game.Core.Managers.Id.IItemIdManager
    {
        private ulong _next = 1;
        public bool Initialize(bool forceReset = false) => true;
        public void Load() { }
        public uint GetNextId() => unchecked((uint)_next++);
        public uint[] GetNextId(int count) => Enumerable.Range(0, count).Select(_ => GetNextId()).ToArray();
        public void ReleaseId(uint usedObjectId) { }
        public void ReleaseId(IEnumerable<uint> usedObjectIds) { }
    }

    /// <summary>TaskManager rigged in SeedSingletons and registered as the singleton
    /// (Task.Cancel() resolves Singleton&lt;TaskManager&gt;.Instance - CheckTimer paths).</summary>
    private static TaskManager s_taskManager;

    /// <summary>Snapshot of the quest state captured for the PERSIST round-trip.</summary>
    public sealed record PersistSnapshot(byte[] Data, QuestComponentKind Step, QuestAcceptorType AcceptorType,
        uint AcceptorId, uint ComponentId, int[] Objectives);

    #region Singleton rig

    /// <summary>
    /// Seeds the singletons the quest engine needs during a driven scenario.
    /// Safe to call repeatedly (idempotent). Must run before any Quest construction.
    /// </summary>
    /// <param name="itemTemplates">Item templates to register (reward items must be present so
    /// supply acts can actually add them to the rigged inventory; MaxCount defaults to 100).</param>
    public static void SeedSingletons(Dictionary<uint, ItemTemplate> itemTemplates = null)
    {
        // QuestManager: template parts are registered later by BuildTemplate().
        var questManager = new QuestManager(Mock.Of<ITaskManager>().Object, Mock.Of<IZoneManager>().Object);
        SetField(questManager, "_componentTemplates", new Dictionary<uint, QuestComponentTemplate>());
        SetField(questManager, "_groupItems", new Dictionary<uint, List<uint>>());
        SetField(questManager, "_groupNpcs", new Dictionary<uint, List<uint>>());
        SetSingleton(typeof(Singleton<QuestManager>), questManager);

        // ItemManager: empty template table (reward items get registered by BuildTemplate()).
        // The item-id manager must return UNIQUE incrementing ids (a default mock returns
        // 0 for every item, so the second created item collides in the live-item registry
        // and reward distribution silently fails - seen as "reward item found 0" in tier
        // runs where many quests share one process).
        var itemManager = new ItemManager(
            Mock.Of<ISkillManager>().Object,
            new IncrementingIdManager(),
            Mock.Of<IContainerIdManager>().Object,
            Mock.Of<ILocalizationManager>().Object,
            Mock.Of<ITaskManager>().Object,
            Mock.Of<IWorldManager>().Object);
        SetField(itemManager, "_templates", itemTemplates ?? new Dictionary<uint, ItemTemplate>());
        // Load()-time state the item-creation path touches (Create/AcquireDefaultItem):
        // item id removed-queue + live item registry. Both are assigned in Load(),
        // which we do not run - seed empty collections so GetNewId()/Create() work.
        SetField(itemManager, "_removedItems", new List<ulong>());
        SetField(itemManager, "_allItems", new Dictionary<ulong, Item>());
        SetSingleton(typeof(Singleton<ItemManager>), itemManager);

        // UnitRequirementsGameData: empty requirement sets -> every component is runnable.
        // The owner-type key map MUST contain the keys the quest path looks up
        // (GetRequirement returns null for a missing key and the callers .ToList()
        // it, which throws) - seed every known owner type with an empty list.
        var unitRequirements = new UnitRequirementsGameData();
        var ownerTypes = new[] { "QuestComponent", "Sphere", "Skill", "ItemArmor", "ItemWeapon", "AchievementObjective", "AiEvent" };
        SetField(unitRequirements, "<_unitReqs>k__BackingField", new Dictionary<uint, UnitReqs>());
        SetField(unitRequirements, "<_unitReqsByOwnerType>k__BackingField",
            ownerTypes.ToDictionary(t => t, _ => new List<UnitReqs>()));
        SetSingleton(typeof(Singleton<UnitRequirementsGameData>), unitRequirements);

        // QuestIdManager: initialized so GetNextId()/ReleaseId() work without a database.
        QuestIdManager.Instance.Initialize(true);

        // TeamManager: two T1 talk acts carry team_share=1 and the talk handler
        // touches TeamManager.Instance when the flag is set - seed a mock-backed
        // instance so GetTeamByObjId returns null (no teams) instead of throwing.
        SetSingleton(typeof(Singleton<TeamManager>),
            new TeamManager(Mock.Of<IWorldManager>().Object, Mock.Of<IChatManager>().Object, Mock.Of<ITeamIdManager>().Object));

        // TaskManager: Task.Cancel() (CheckTimer cleanup paths) resolves the
        // Singleton<TaskManager>.Instance - register the rigged instance so the
        // singleton init does not demand a parameterless constructor.
        var mockTickManager = Mock.Of<ITickManager>();
        mockTickManager.OnTick.Returns(new TickManager.TickEventHandler());
        s_taskManager = new TaskManager(mockTickManager.Object);
        SetSingleton(typeof(Singleton<TaskManager>), s_taskManager);

        // ExperienceManager: QuestActSupplyExp -> Character.AddExp -> GetLevelFromExp
        // binary-searches the exp table. Seed a sparse curve with huge per-level steps
        // (100M) so small quest rewards never trigger a level-up (which would route
        // through BroadcastPacket / world state that is not rigged here).
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
        SetSingleton(typeof(Singleton<ExperienceManager>), experienceManager);

        // AppConfiguration.World must be non-null: AddExp reads World.ExpRate.
        AppConfiguration.Instance.World ??= new WorldConfig();

        // Debug chat messages must not fire in tests: SendDebugMessage routes through
        // CharacterManager.Instance (DI singleton, not available here). The test host's
        // DI may carry a config with DebugInfo=true, so force it off explicitly.
        AppConfiguration.Instance.DebugInfo = false;
    }

    private static void SetField(object target, string fieldName, object value)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
        if (field == null)
            throw new InvalidOperationException($"Cannot locate field '{fieldName}' on {target.GetType().Name}");
        field.SetValue(target, value);
    }

    private static void SetSingleton(Type singletonBase, object instance)
    {
        var field = singletonBase.GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static);
        if (field == null)
            throw new InvalidOperationException($"Cannot locate singleton field on {singletonBase.Name}");
        field.SetValue(null, instance);
    }

    #endregion

    #region Template building

    /// <summary>
    /// Builds a QuestTemplate from the manifest's template parts and registers the
    /// components in the seeded QuestManager singleton so the Quest constructor's
    /// CreateQuestSteps() can resolve the act templates (same read path the loaders
    /// use, without touching QuestManager.Load).
    /// </summary>
    public static QuestTemplate BuildTemplate(QuestScenarioManifest manifest)
    {
        var template = new QuestTemplate
        {
            Id = manifest.QuestId,
            Level = manifest.Template.Level,
            Selective = manifest.Selective,
            Score = manifest.Score,
            LetItDone = manifest.LetItDone
        };

        var actId = FirstSyntheticActId;
        var selectiveIndex = 0; // mirrors the loader: 1-based, cumulative across components
        byte objectiveIndex = 0;
        var lastKind = QuestComponentKind.None;
        // RC-7 fidelity: mirrors QuestManager.LoadQuestComponents, which reads
        // quest_components ORDER BY quest_context_id, component_kind_id, id -
        // components iterate KIND-GROUPED (all Start, then Supply, then Progress,
        // ...), and UpdateQuestComponentActs (QuestManager.cs:207-211) resets the
        // objective counter on every kind change. Iterating + inserting in the
        // same order means multi-component steps (266/1033/3656/1897) land each
        // objective act in the exact Objectives slot the real loader assigns.
        foreach (var componentShape in manifest.Template.Components
                     .Select(c => (Shape: c, Kind: Enum.Parse<QuestComponentKind>(c.Kind, ignoreCase: true)))
                     .OrderBy(t => t.Kind).ThenBy(t => t.Shape.Id))
        {
            var kind = componentShape.Kind;
            if (kind != lastKind)
            {
                objectiveIndex = 0;
                lastKind = kind;
            }

            var component = new QuestComponentTemplate(template) { Id = componentShape.Shape.Id, KindId = kind };

            foreach (var actElement in componentShape.Shape.Acts)
            {
                var act = BuildAct(component, actElement);
                act.ActId = actId++;
                // Mirrors the loader (QuestManager.cs:220): objective acts take the
                // running per-kind index, every other act gets 0xFF (never an
                // Objectives slot - QuestAct.RunAct guards the read).
                act.ThisComponentObjectiveIndex = act.CountsAsAnObjective ? objectiveIndex++ : (byte)0xFF;
                if (act is QuestActSupplySelectiveItem selective)
                    selective.ThisSelectiveIndex = ++selectiveIndex;
                component.ActTemplates.Add(act);
            }

            template.Components[component.Id] = component;
        }

        // Register components so QuestComponent resolves acts via the singleton.
        var componentsField = typeof(QuestManager).GetField("_componentTemplates", BindingFlags.NonPublic | BindingFlags.Instance);
        var registered = (Dictionary<uint, QuestComponentTemplate>)componentsField.GetValue(QuestManager.Instance);
        foreach (var (componentId, component) in template.Components)
            registered[componentId] = component;

        return template;
    }

    /// <summary>
    /// Act factory: maps a manifest act object ({"type": "...", ...params}) to the
    /// production act class. Unsupported types throw NotSupportedException so tier
    /// manifests fail loudly instead of silently skipping acts.
    /// </summary>
    public static QuestActTemplate BuildAct(QuestComponentTemplate component, JsonElement raw)
    {
        var type = raw.GetProperty("type").GetString();
        var count = GetInt(raw, "count", 1);

        QuestActTemplate act = type switch
        {
            nameof(QuestActConAcceptNpc) => new QuestActConAcceptNpc(component) { NpcId = GetUInt(raw, "npcId") },
            nameof(QuestActConAcceptNpcKill) => new QuestActConAcceptNpcKill(component) { NpcId = GetUInt(raw, "npcId") },
            nameof(QuestActConAcceptDoodad) => new QuestActConAcceptDoodad(component) { DoodadId = GetUInt(raw, "doodadId") },
            nameof(QuestActConAcceptItem) => new QuestActConAcceptItem(component) { ItemId = GetUInt(raw, "itemId") },
            nameof(QuestActConAcceptSphere) => new QuestActConAcceptSphere(component) { SphereId = GetUInt(raw, "sphereId") },
            nameof(QuestActConAcceptLevelUp) => new QuestActConAcceptLevelUp(component) { Level = GetByte(raw, "level") },
            nameof(QuestActConAcceptComponent) => new QuestActConAcceptComponent(component),
            nameof(QuestActConAutoComplete) => new QuestActConAutoComplete(component),
            nameof(QuestActConReportNpc) => new QuestActConReportNpc(component) { NpcId = GetUInt(raw, "npcId") },
            nameof(QuestActConReportDoodad) => new QuestActConReportDoodad(component) { DoodadId = GetUInt(raw, "doodadId") },
            nameof(QuestActConReportJournal) => new QuestActConReportJournal(component),
            nameof(QuestActObjMonsterHunt) => new QuestActObjMonsterHunt(component) { NpcId = GetUInt(raw, "npcId"), Count = count },
            nameof(QuestActObjMonsterGroupHunt) => new QuestActObjMonsterGroupHunt(component) { QuestMonsterGroupId = GetUInt(raw, "monsterGroupId"), Count = count },
            nameof(QuestActObjItemGather) => new QuestActObjItemGather(component) { ItemId = GetUInt(raw, "itemId"), Count = count },
            nameof(QuestActObjItemUse) => new QuestActObjItemUse(component) { ItemId = GetUInt(raw, "itemId"), Count = count },
            nameof(QuestActObjItemGroupGather) => new QuestActObjItemGroupGather(component) { ItemGroupId = GetUInt(raw, "itemGroupId"), Count = count },
            nameof(QuestActObjItemGroupUse) => new QuestActObjItemGroupUse(component) { ItemGroupId = GetUInt(raw, "itemGroupId"), Count = count },
            nameof(QuestActObjTalk) => new QuestActObjTalk(component) { NpcId = GetUInt(raw, "npcId"), Count = count },
            nameof(QuestActObjTalkNpcGroup) => new QuestActObjTalkNpcGroup(component) { NpcGroupId = GetUInt(raw, "npcGroupId"), Count = count },
            nameof(QuestActObjInteraction) => new QuestActObjInteraction(component) { DoodadId = GetUInt(raw, "doodadId"), Count = count },
            nameof(QuestActObjSphere) => new QuestActObjSphere(component) { SphereId = GetUInt(raw, "sphereId"), Count = count },
            nameof(QuestActObjCraft) => new QuestActObjCraft(component) { CraftId = GetUInt(raw, "craftId"), Count = count },
            nameof(QuestActObjLevel) => new QuestActObjLevel(component) { Level = GetByte(raw, "level"), Count = count },
            nameof(QuestActObjZoneMonsterHunt) => new QuestActObjZoneMonsterHunt(component) { ZoneId = GetUInt(raw, "zoneId"), Count = count },
            nameof(QuestActObjExpressFire) => new QuestActObjExpressFire(component) { ExpressKeyId = GetUInt(raw, "expressKeyId"), NpcGroupId = GetUInt(raw, "npcGroupId"), Count = count },
            nameof(QuestActCheckGuard) => new QuestActCheckGuard(component) { NpcId = GetUInt(raw, "npcId") },
            nameof(QuestActCheckSphere) => new QuestActCheckSphere(component) { SphereId = GetUInt(raw, "sphereId") },
            nameof(QuestActCheckTimer) => new QuestActCheckTimer(component) { LimitTime = GetInt(raw, "limitTime"), NextComponent = GetUInt(raw, "nextComponent") },
            nameof(QuestActSupplyItem) => new QuestActSupplyItem(component) { ItemId = GetUInt(raw, "itemId"), GradeId = GetByte(raw, "gradeId"), Count = count },
            nameof(QuestActSupplyCopper) => new QuestActSupplyCopper(component) { Amount = GetInt(raw, "amount") },
            nameof(QuestActSupplyExp) => new QuestActSupplyExp(component) { Exp = GetInt(raw, "exp") },
            nameof(QuestActSupplyJuryPoint) => new QuestActSupplyJuryPoint(component) { Point = GetInt(raw, "point") },
            nameof(QuestActSupplyAppellation) => new QuestActSupplyAppellation(component) { AppellationId = GetUInt(raw, "appellationId") },
            nameof(QuestActSupplyRemoveItem) => new QuestActSupplyRemoveItem(component) { ItemId = GetUInt(raw, "itemId"), Count = count },
            nameof(QuestActSupplySelectiveItem) => new QuestActSupplySelectiveItem(component) { ItemId = GetUInt(raw, "itemId"), GradeId = GetByte(raw, "gradeId"), Count = count },
            _ => throw new NotSupportedException(
                $"Unsupported act type '{type}' in scenario manifest (quest {component.ParentQuestTemplate.Id}); " +
                "add it to QuestScenarioDriver.BuildAct or fix the manifest")
        };

        if (raw.TryGetProperty("detailId", out var detailIdElement) && detailIdElement.TryGetUInt32(out var detailId))
            act.DetailId = detailId;
        return act;
    }

    #endregion

    #region Quest building

    /// <summary>
    /// Builds the full rig for one scenario run: template from manifest parts,
    /// real Character owner with rigged inventory + CharacterQuests + parent world
    /// (guard NPC spawned when the manifest asks for one), and the Quest itself.
    /// </summary>
    public static Quest BuildQuest(QuestScenarioManifest manifest)
    {
        var template = BuildTemplate(manifest);

        var character = new Character(new UnitCustomModelParams())
        {
            ObjId = 1,
            Id = 1,
            Name = "ScenarioTester",
            Level = manifest.Template.Level,
            Inventory = CreateInventory()
        };
        character.Appellations = new CharacterAppellations(character);
        character.Abilities = new CharacterAbilities(character);
        // RC-6 (t_2d482bc3): rig real abilities. Ability1..3 auto-properties
        // default to AbilityType.General (0), which the CharacterAbilities ctor
        // never seeds (it seeds Fight(1)..Love(10)) - without this, a
        // QuestActSupplyExp reward (Character.AddExp -> AddActiveExp) hits the
        // unseeded General key and the exp is silently dropped. Three distinct
        // seeded abilities mirror a real character so reward exp actually lands.
        character.Ability1 = AbilityType.Fight;
        character.Ability2 = AbilityType.Magic;
        character.Ability3 = AbilityType.Will;
        character.Quests = new CharacterQuests(character);

        // Seed item-group / npc-group membership for group acts (BUG-009 read path).
        if (manifest.Groups != null)
        {
            var questManager = QuestManager.Instance;
            var groupItemsField = typeof(QuestManager).GetField("_groupItems", BindingFlags.NonPublic | BindingFlags.Instance);
            var groupNpcsField = typeof(QuestManager).GetField("_groupNpcs", BindingFlags.NonPublic | BindingFlags.Instance);
            var groupItems = (Dictionary<uint, List<uint>>)groupItemsField.GetValue(questManager);
            var groupNpcs = (Dictionary<uint, List<uint>>)groupNpcsField.GetValue(questManager);
            foreach (var (groupId, members) in manifest.Groups.ItemGroups ?? [])
                groupItems[groupId] = members;
            foreach (var (groupId, members) in manifest.Groups.NpcGroups ?? [])
                groupNpcs[groupId] = members;
        }

        // Pre-stock the rigged inventory (acceptor items, gather objectives).
        if (manifest.Inventory is { Count: > 0 })
        {
            var slot = 0u;
            foreach (var stockItem in manifest.Inventory)
            {
                for (var i = 0; i < stockItem.Count; i++)
                    character.Inventory.Bag.Items.Add(new ItemMock(++slot, new ItemTemplate { Id = stockItem.ItemId }, 1));
            }
        }

        // Parent world rig (guard/sphere-capable). Set via the backing field because
        // the property setter routes through WorldManager.Instance (not available in tests).
        var worldTemplate = new WorldTemplate
        {
            Id = 1,
            Name = "scenario_world",
            ZoneKeys = [],
            CellX = 2,
            CellY = 2,
            ZoneKeyByRegions = new uint[32, 32]
        };
        var world = new WorldInstance(worldTemplate, 0, true, 1);
        // Sphere quest triggers (QuestActObjSphere/CheckSphere initialize through
        // ParentWorld.SphereQuestManager - must be non-null or InitializeAction throws).
        world.SphereQuestManager = new SphereQuestManager(world);
        // The manager's quest lookup table is only populated by Load() - seed the
        // static dictionary so GetQuestSpheres() does not NRE on a null dictionary.
        var spheresField = typeof(SphereQuestManager).GetField("_sphereQuests", BindingFlags.NonPublic | BindingFlags.Static);
        if (spheresField?.GetValue(null) == null)
            spheresField.SetValue(null, new Dictionary<uint, List<SphereQuest>>());
        var parentWorldField = typeof(GameObject).GetField("_parentWorld", BindingFlags.NonPublic | BindingFlags.Instance);
        parentWorldField?.SetValue(character, world);

        // Spawn a guard NPC for every QuestActCheckGuard act in ANY component
        // (RC-3): QuestActCheckGuard.RunAct resolves the NPC via
        // ParentWorld.GetNpcByTemplateId and returns false when it is missing
        // (CheckGuard.cs:26-33), so a CheckGuard in a non-Start component could
        // never pass without its rig. Dedupe by NpcId - one world NPC per guard
        // template id (manifest guard blocks are overrides on top of the acts).
        var guardIds = new List<uint>();
        void AddGuard(uint npcId)
        {
            if (npcId != 0 && !guardIds.Contains(npcId))
                guardIds.Add(npcId);
        }
        foreach (var component in template.Components.Values)
            foreach (var act in component.ActTemplates)
                if (act is QuestActCheckGuard checkGuard)
                    AddGuard(checkGuard.NpcId);
        if (manifest.Guard != null)
            AddGuard(manifest.Guard.NpcId);
        foreach (var guardShape in manifest.Guards ?? [])
            AddGuard(guardShape.NpcId);

        var guardObjId = 100u;
        foreach (var guardNpcId in guardIds)
        {
            var alive = true;
            if (manifest.Guard?.NpcId == guardNpcId)
                alive = manifest.Guard.Alive;
            foreach (var guardShape in manifest.Guards ?? [])
                if (guardShape.NpcId == guardNpcId)
                    alive = guardShape.Alive;
            var guard = new Npc
            {
                ObjId = guardObjId++,
                TemplateId = guardNpcId,
                Hp = alive ? 100 : 0,
                MaxHp = 100
            };
            world.AddObject(guard);
        }

        // CheckSphere rig (quest 1033): since BUG-011, QuestActCheckSphere.RunAct
        // evaluates the owner's LIVE position against the component's quest
        // spheres (SphereQuestManager.GetQuestSpheres) - without a world server
        // those never load, so the check could never pass. Register one
        // origin-centered sphere per CheckSphere component; the rigged character
        // has no transform, so its position reads Vector3.Zero and the check
        // resolves. Same harness-only class of rig as the guard spawn above.
        foreach (var component in template.Components.Values)
        {
            foreach (var act in component.ActTemplates)
            {
                if (act is not QuestActCheckSphere)
                    continue;
                var checkSphereField = typeof(SphereQuestManager).GetField("_sphereQuests", BindingFlags.NonPublic | BindingFlags.Static);
                var sphereQuests = (Dictionary<uint, List<SphereQuest>>)checkSphereField?.GetValue(null);
                if (sphereQuests != null && !sphereQuests.ContainsKey(component.Id))
                    sphereQuests[component.Id] =
                    [
                        new SphereQuest
                        {
                            QuestId = template.Id,
                            ComponentId = component.Id,
                            Xyz = Vector3.Zero,
                            Radius = 100f
                        }
                    ];
            }
        }

        var mockTickManager = Mock.Of<ITickManager>();
        mockTickManager.OnTick.Returns(new TickManager.TickEventHandler());

        var quest = new Quest(
            template,
            character,
            QuestManager.Instance,
            s_taskManager,
            Mock.Of<ISkillManager>().Object,
            Mock.Of<IExpressTextManager>().Object,
            Mock.Of<IWorldManager>().Object)
        {
            Id = QuestIdManager.Instance.GetNextId(),
            QuestAcceptorType = Enum.Parse<QuestAcceptorType>(manifest.Acceptor.Type, ignoreCase: true),
            AcceptorId = manifest.Acceptor.Id,
            Condition = QuestConditionObj.Progress,
            SelectedRewardIndex = manifest.SelectedRewardIndex
        };

        return quest;
    }

    /// <summary>
    /// Builds an Inventory (bypassing the ItemManager singleton) with an empty bag.
    /// Mirrors the rig from QuestActObjItemGroupGatherTests, plus sets the Bag
    /// auto-property backing field (DistributeRewards reads Owner.Inventory.Bag
    /// directly, not via _itemContainers).
    /// </summary>
    private static Inventory CreateInventory()
    {
        var inventory = (Inventory)FormatterServices.GetUninitializedObject(typeof(Inventory));
        // ownerId must be 0: ItemContainer.Owner resolves via WorldManager.Instance
        // when _ownerId > 0 and no owner is set (not available in tests); with 0 the
        // Owner getter short-circuits to null and the acquire path is null-safe.
        var bag = new ItemContainer(0, SlotType.Inventory, createWithNewId: false, null);
        var containersField = typeof(Inventory).GetField("<_itemContainers>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance)
                              ?? typeof(Inventory).GetField("_itemContainers", BindingFlags.NonPublic | BindingFlags.Instance);
        containersField?.SetValue(inventory, new Dictionary<SlotType, ItemContainer> { [SlotType.Inventory] = bag });
        var bagField = typeof(Inventory).GetField("<Bag>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);
        bagField?.SetValue(inventory, bag);
        return inventory;
    }

    /// <summary>
    /// Registers the item templates referenced by a manifest (supply acts + expected
    /// reward items) into the seeded ItemManager so supply distribution can actually
    /// add items to the rigged inventory.
    /// </summary>
    public static void RegisterManifestItems(QuestScenarioManifest manifest)
    {
        var templatesField = typeof(ItemManager).GetField("_templates", BindingFlags.NonPublic | BindingFlags.Instance);
        var templates = (Dictionary<uint, ItemTemplate>)templatesField.GetValue(ItemManager.Instance);

        void Register(uint itemId)
        {
            if (itemId != 0 && !templates.ContainsKey(itemId))
                templates[itemId] = new ItemTemplate { Id = itemId, MaxCount = 100 };
        }

        foreach (var component in manifest.Template.Components)
        {
            foreach (var actElement in component.Acts)
            {
                if (actElement.TryGetProperty("type", out var typeElement) &&
                    typeElement.GetString() is "QuestActSupplyItem" or "QuestActSupplySelectiveItem" or "QuestActSupplyRemoveItem")
                {
                    Register(GetUInt(actElement, "itemId"));
                }
            }
        }

        foreach (var stage in manifest.Stages)
        {
            if (stage.Expect.RewardItems != null)
            {
                foreach (var rewardItem in stage.Expect.RewardItems)
                    Register(rewardItem.ItemId);
            }
        }

        // Pre-stocked inventory items (acceptor items, gather objectives) must resolve
        // in the item template table for count checks / gather reads.
        foreach (var stockItem in manifest.Inventory ?? [])
            Register(stockItem.ItemId);
    }

    #endregion

    #region Synthetic events

    /// <summary>
    /// Fires one synthetic gameplay event on the quest owner's UnitEvents. Event
    /// types are the quest act family names (see QuestScenarioManifest doc header).
    /// </summary>
    public static void FireEvent(Quest quest, JsonElement rawEvent)
    {
        var type = rawEvent.GetProperty("type").GetString();
        var owner = quest.Owner;

        switch (type)
        {
            case "MonsterHunt":
                owner.Events.OnMonsterHunt(owner, new OnMonsterHuntArgs { NpcId = GetUInt(rawEvent, "npcId"), Count = GetUInt(rawEvent, "count", 1) });
                break;
            case "MonsterGroupHunt":
                owner.Events.OnMonsterGroupHunt(owner, new OnMonsterGroupHuntArgs { NpcId = GetUInt(rawEvent, "npcId"), Count = GetUInt(rawEvent, "count", 1) });
                break;
            case "ItemGather":
                owner.Events.OnItemGather(owner, new OnItemGatherArgs { QuestId = quest.TemplateId, ItemId = GetUInt(rawEvent, "itemId"), Count = GetInt(rawEvent, "count", 1) });
                break;
            case "ItemGroupGather":
                owner.Events.OnItemGroupGather(owner, new OnItemGroupGatherArgs { ItemId = GetUInt(rawEvent, "itemId"), Count = GetInt(rawEvent, "count", 1), ItemGroupId = GetUInt(rawEvent, "itemGroupId") });
                break;
            case "ItemUse":
                // RC-4: item-use acts credit +1 per event (QuestActObjItemUse.cs:46,
                // QuestActObjItemGroupUse.cs:58) - OnItemUseArgs carries no Count, so
                // one use = one event. Fire 'count' times (default 1).
                for (var i = 0; i < GetInt(rawEvent, "count", 1); i++)
                    owner.Events.OnItemUse(owner, new OnItemUseArgs { ItemId = GetUInt(rawEvent, "itemId") });
                break;
            case "ItemGroupUse":
                owner.Events.OnItemGroupUse(owner, new OnItemGroupUseArgs { ItemGroupId = GetUInt(rawEvent, "itemGroupId"), Count = GetInt(rawEvent, "count", 1) });
                break;
            case "Talk":
                owner.Events.OnTalkMade(owner, new OnTalkMadeArgs { QuestId = quest.TemplateId, NpcId = GetUInt(rawEvent, "npcId"), SourcePlayer = owner });
                break;
            case "TalkNpcGroup":
                owner.Events.OnTalkNpcGroupMade(owner, new OnTalkNpcGroupMadeArgs { NpcGroupId = GetUInt(rawEvent, "npcGroupId"), NpcId = GetUInt(rawEvent, "npcId"), QuestComponentId = GetUInt(rawEvent, "componentId") });
                break;
            case "Interaction":
                owner.Events.OnInteraction(owner, new OnInteractionArgs { DoodadId = GetUInt(rawEvent, "doodadId"), SourcePlayer = owner });
                break;
            case "EnterSphere":
                owner.Events.OnEnterSphere(owner, new OnEnterSphereArgs
                {
                    SphereQuest = new SphereQuest
                    {
                        QuestId = quest.TemplateId,
                        ComponentId = GetUInt(rawEvent, "componentId"),
                        Radius = 100f
                    }
                });
                break;
            case "Craft":
                owner.Events.OnCraft(owner, new OnCraftArgs { CraftId = GetUInt(rawEvent, "craftId") });
                break;
            case "ReportNpc":
                owner.Events.OnReportNpc(owner, new OnReportNpcArgs { QuestId = quest.TemplateId, NpcId = GetUInt(rawEvent, "npcId"), Selected = GetInt(rawEvent, "selected") });
                break;
            case "ReportDoodad":
                owner.Events.OnReportDoodad(owner, new OnReportDoodadArgs { QuestId = quest.TemplateId, DoodadId = GetUInt(rawEvent, "doodadId"), Selected = GetInt(rawEvent, "selected") });
                break;
            case "ReportJournal":
                owner.Events.OnReportJournal(owner, new OnReportJournalArgs());
                break;
            case "ExpressFire":
                owner.Events.OnExpressFire(owner, new OnExpressFireArgs { NpcId = GetUInt(rawEvent, "npcId"), EmotionId = GetUInt(rawEvent, "emotionId") });
                break;
            case "LevelUp":
                // ObjLevel objectives check Owner.Level >= Level (QuestActObjLevel.cs:23/46)
                // and OnLevelUpArgs carries no level - raise the owner to the quest's
                // highest ObjLevel requirement so the objective becomes reachable
                // mid-quest (harness-only calibration; quest 6250 template level is 0
                // while its Progress act demands level 30).
                var requiredLevel = quest.Template.Components.Values
                    .SelectMany(c => c.ActTemplates)
                    .OfType<QuestActObjLevel>()
                    .Select(a => a.Level)
                    .DefaultIfEmpty((byte)0)
                    .Max();
                if (requiredLevel > owner.Level)
                    owner.Level = requiredLevel;
                owner.Events.OnLevelUp(owner, new OnLevelUpArgs());
                break;
            case "Aggro":
                owner.Events.OnAggro(owner, new OnAggroArgs { NpcId = GetUInt(rawEvent, "npcId") });
                break;
            case "ZoneKill":
                owner.Events.OnZoneKill(owner, new OnZoneKillArgs { ZoneGroupId = GetUInt(rawEvent, "zoneGroupId"), Killer = owner, Victim = (Unit)owner });
                break;
            default:
                throw new NotSupportedException($"Unsupported event type '{type}' in scenario manifest (quest {quest.TemplateId})");
        }
    }

    #endregion

    #region Lifecycle

    /// <summary>
    /// Drives the full manifest lifecycle and returns the per-stage verdict record.
    /// START semantics mirror CharacterQuests.AddQuest: accept (StartQuest), register
    /// in ActiveQuests, run the first step evaluation.
    /// </summary>
    public QuestScenarioVerdict Run(QuestScenarioManifest manifest)
    {
        var verdict = new QuestScenarioVerdict { QuestId = manifest.QuestId, Name = manifest.Name };

        // Skip-with-reason (broken refs, unsupported shapes) - reported, never run.
        if (manifest.Skip != null)
        {
            verdict.Overall = StageOutcome.Skip;
            verdict.Stages.Add(new QuestScenarioStageVerdict
            {
                Stage = "SKIP",
                Outcome = StageOutcome.Skip,
                Reason = manifest.Skip.Reason
            });
            return verdict;
        }

        Quest quest;
        try
        {
            quest = BuildQuest(manifest);
        }
        catch (Exception ex)
        {
            verdict.Overall = StageOutcome.Fail;
            verdict.Stages.Add(QuestScenarioAssertions.Fail("BUILD", $"quest construction failed: {ex.GetType().Name}: {ex.Message}"));
            return verdict;
        }

        var character = (Character)quest.Owner;
        PersistSnapshot persistSnapshot = null;

        try
        {
            // Accept the quest (mirror AddQuest): StartQuest + register + first evaluation.
            if (!quest.StartQuest())
                throw new InvalidOperationException("StartQuest() returned false - quest has no Start component");
            character.Quests.ActiveQuests.Add(quest.TemplateId, quest);
            quest.RunCurrentStep();
        }
        catch (Exception ex)
        {
            verdict.Overall = StageOutcome.Fail;
            verdict.Stages.Add(QuestScenarioAssertions.Fail("START", $"accept failed: {ex}"));
            return verdict;
        }

        foreach (var stage in manifest.Stages)
        {
            QuestScenarioStageVerdict stageVerdict;
            Quest persistFreshQuest = null;

            try
            {
                switch (stage.Name.ToUpperInvariant())
                {
                    case "PERSIST":
                        if (persistSnapshot == null)
                            throw new InvalidOperationException("PERSIST stage has no snapshot - it must follow a non-terminal stage");
                        persistFreshQuest = BuildQuest(manifest);
                        persistFreshQuest.ReadData(persistSnapshot.Data);
                        break;
                    default:
                        foreach (var rawEvent in stage.Events)
                            FireEvent(quest, rawEvent);
                        quest.RunCurrentStep();
                        break;
                }

                // Snapshot for the PERSIST round-trip: capture after every stage that
                // leaves the quest alive in a meaningful state (REWARD drops the quest).
                if (stage.Name.ToUpperInvariant() is not ("PERSIST" or "REWARD"))
                {
                    persistSnapshot = new PersistSnapshot(
                        quest.WriteData(), quest.Step, quest.QuestAcceptorType, quest.AcceptorId,
                        quest.ComponentId, (int[])quest.Objectives.Clone());
                }

                stageVerdict = QuestScenarioAssertions.EvaluateStage(
                    manifest, quest, character, stage, persistSnapshot?.Data, persistFreshQuest, persistSnapshot);
                // Record the engine's actual state at evaluation time (calibration diagnostics).
                stageVerdict.StepObserved = quest.Step;
                stageVerdict.StatusObserved = quest.Status;
            }
            catch (Exception ex)
            {
                stageVerdict = QuestScenarioAssertions.Fail(stage.Name, $"{ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            }

            verdict.Stages.Add(stageVerdict);
            if (stageVerdict.Outcome == StageOutcome.Fail)
                verdict.Overall = StageOutcome.Fail;
            verdict.QuestRef = quest;
            }

        if (verdict.Overall == StageOutcome.NotRun && verdict.Stages.Count > 0)
        {
            // Skip-with-reason is not a failure: an observation stage must not sink
            // the overall verdict.
            verdict.Overall = verdict.Stages.All(s => s.Outcome is StageOutcome.Pass or StageOutcome.Skip)
                ? StageOutcome.Pass
                : StageOutcome.Fail;
        }

        return verdict;
    }

    #endregion

    #region JsonElement helpers

    private static uint GetUInt(JsonElement element, string name, uint defaultValue = 0)
    {
        return element.TryGetProperty(name, out var value) && value.TryGetUInt32(out var result) ? result : defaultValue;
    }

    private static int GetInt(JsonElement element, string name, int defaultValue = 0)
    {
        return element.TryGetProperty(name, out var value) && value.TryGetInt32(out var result) ? result : defaultValue;
    }

    private static byte GetByte(JsonElement element, string name, byte defaultValue = 0)
    {
        return element.TryGetProperty(name, out var value) && value.TryGetByte(out var result) ? result : defaultValue;
    }

    #endregion
}
