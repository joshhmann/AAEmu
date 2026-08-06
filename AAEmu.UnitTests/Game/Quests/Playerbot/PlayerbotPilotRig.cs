using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Utils.DB;
using AAEmu.UnitTests.Game.Quests.Scenario;
using AAEmu.UnitTests.Utils.Mocks;

namespace AAEmu.UnitTests.Game.Quests.Playerbot;

/// <summary>
/// M2b pilot rig: seeds the singleton surface the real quest engine needs,
/// loads REAL quest templates + REAL unit requirements from the canonical
/// compact.sqlite3 (the same data prod boots with), and builds headless bots.
///
/// Difference vs the scenario harness: the scenario driver synthesizes
/// templates from manifest parts and rigs EMPTY unit requirements; the pilot
/// runs QuestManager.Load() + UnitRequirementsGameData.Load() so accept gates
/// (level, race, quest-completion chains, repeatable checks) are the REAL
/// prod gates.
/// </summary>
public static class PlayerbotPilotRig
{
    private static bool s_seeded;
    private static readonly object s_seedLock = new();

    /// <summary>
    /// Seeds every singleton the AddQuest -> new Quest(template, owner) path
    /// resolves. Idempotent; must run before any bot is created.
    /// </summary>
    public static void SeedPilotSingletons()
    {
        lock (s_seedLock)
        {
            if (s_seeded)
                return;

            // Base rig (mocked QuestManager with empty tables, ItemManager,
            // QuestIdManager, TeamManager, TaskManager, ExperienceManager,
            // AccountManager, empty UnitRequirementsGameData).
            QuestScenarioDriver.SeedSingletons();

            // The Quest ctor used by AddQuest resolves
            // SkillManager.Instance / ExpressTextManager.Instance /
            // WorldManager.Instance (DI singletons with no parameterless
            // ctor) - seed mock-backed instances so the singleton init
            // never demands a parameterless constructor.
            SetSingleton(typeof(Singleton<SkillManager>),
                new SkillManager(Mock.Of<IAnimationManager>().Object, Mock.Of<IPlotManager>().Object));
            SetSingleton(typeof(Singleton<WorldManager>),
                new WorldManager(
                    Mock.Of<ITickManager>().Object,
                    Mock.Of<IWorldIdManager>().Object,
                    new Lazy<IZoneManager>(() => Mock.Of<IZoneManager>().Object),
                    new Lazy<IIndunManager>(() => Mock.Of<IIndunManager>().Object),
                    new Lazy<IFamilyManager>(() => Mock.Of<IFamilyManager>().Object)));
            // ExpressTextManager has a parameterless ctor - singleton init is safe.

            // ContainerIdManager: the real Inventory ctor allocates container ids
            // via ContainerIdManager.Instance.GetNextId() (ItemContainer.cs:154).
            // Initialize fails closed on the missing MySQL (logged, empty used
            // ids) and then serves incrementing ids from its range.
            AAEmu.Game.Core.Managers.Id.ContainerIdManager.Instance.Initialize(true);

            // Persistent container registry: the real Inventory ctor calls
            // ItemManager.GetItemContainerForCharacter, which iterates
            // _allPersistentContainers - the scenario rig never seeds it
            // (its inventory bypasses ItemManager entirely). Seed empty so the
            // ordinary Character construction path works.
            var containerField = typeof(ItemManager).GetField("_allPersistentContainers",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            containerField?.SetValue(ItemManager.Instance,
                new Dictionary<ulong, AAEmu.Game.Models.Game.Items.Containers.ItemContainer>());

            // REAL data: quest templates + unit requirements from the
            // canonical DB. QuestManager.Load() opens FileManager.AppPath/Data/
            // compact.sqlite3 itself; ensure the file is present where the
            // host resolves AppPath (copy from the test assembly output).
            EnsureCompactDatabase();

            QuestManager.Instance.Load();
            UnitRequirementsGameData.Instance.Load(SQLite.CreateConnection());

            s_seeded = true;
        }
    }

    /// <summary>
    /// Copies compact.sqlite3 from the test assembly's Data dir into whatever
    /// directory FileManager.AppPath resolves to (the host entrypoint may not
    /// be the test assembly).
    /// </summary>
    private static void EnsureCompactDatabase()
    {
        var source = Path.Combine(AppContext.BaseDirectory, "Data", "compact.sqlite3");
        if (!File.Exists(source))
            throw new InvalidOperationException(
                "Pilot data missing: expected compact.sqlite3 at " + source +
                " (copy the canonical DB from the aaemu box: tools/playerbot-pilot/fetch-data.sh)");

        var appPath = AAEmu.Commons.IO.FileManager.AppPath;
        var dest = Path.Combine(appPath, "Data", "compact.sqlite3");
        if (File.Exists(dest))
            return;
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.Copy(source, dest);
    }

    private static void SetSingleton(Type singletonBase, object instance)
    {
        var field = singletonBase.GetField("s_instance", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
                    ?? throw new InvalidOperationException($"Cannot locate singleton field on {singletonBase.Name}");
        field.SetValue(null, instance);
    }

    /// <summary>
    /// Creates a fresh headless bot (ordinary Character, no Connection).
    /// </summary>
    public static PlayerBotController CreateBot(string name, byte level = 1, Race race = Race.Nuian)
    {
        var session = HeadlessSession.Create((uint)name.GetHashCode() & 0xFFFF, name, level, race);
        return new PlayerBotController(session.Character);
    }

    /// <summary>
    /// Ensures a quest's reward / objective item templates resolve in the
    /// rigged ItemManager (MaxCount 100 like the scenario rig) so supply and
    /// gather paths can create items.
    /// </summary>
    public static void RegisterQuestItems(QuestScenarioManifest manifest)
        => QuestScenarioDriver.RegisterManifestItems(manifest);

    /// <summary>
    /// Seeds item-group / npc-group membership used by group acts.
    /// </summary>
    public static void SeedQuestGroups(QuestScenarioManifest manifest)
    {
        if (manifest.Groups == null)
            return;
        var questManager = QuestManager.Instance;
        var groupItemsField = typeof(QuestManager).GetField("_groupItems", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var groupNpcsField = typeof(QuestManager).GetField("_groupNpcs", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var groupItems = (Dictionary<uint, List<uint>>)groupItemsField!.GetValue(questManager)!;
        var groupNpcs = (Dictionary<uint, List<uint>>)groupNpcsField!.GetValue(questManager)!;
        foreach (var (groupId, members) in manifest.Groups.ItemGroups ?? [])
            groupItems[groupId] = members;
        foreach (var (groupId, members) in manifest.Groups.NpcGroups ?? [])
            groupNpcs[groupId] = members;
    }
}
