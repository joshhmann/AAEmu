using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.Bots.Goap.Actions;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Containers;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.Taxations;
using AAEmu.UnitTests.Game.Core.Managers.Bots;

using Microsoft.Data.Sqlite;

namespace AAEmu.UnitTests.Game.Bots;

/// <summary>
/// Hermetic rig for the production HeadlessSession provisioning contract
/// (t_302b67bf). No MySQL: these tests lock the pilot-fixture boundary and
/// the fail-loudly behavior of the production path without a database. The
/// live provision → activate → persist → deactivate round-trip rides the
/// env-gated live rig (HeadlessSessionProvisioningLiveTests).
/// </summary>
[NotInParallel]
public class HeadlessSessionProvisioningTests
{
    // ---------------------------------------------------------------- pilot fixture boundary

    [Test]
    public async Task Create_PilotFixture_IsSyntheticAndDbFree()
    {
        // The M2b-E2E fixture (DB-row-less, synthetic world) MUST keep working
        // as-is — review correction (b): fixture only, NOT the production
        // citizen path. It must never touch MySQL: no Save, no provisioning.
        SeedFixtureSingletons();
        var session = HeadlessSession.Create(4200001u, "PilotFixtureBot", 1);

        await Assert.That(session.Character.Id).IsEqualTo(4200001u);
        await Assert.That(session.Character.Name).IsEqualTo("PilotFixtureBot");
        await Assert.That(session.Character.Connection).IsNull(); // no network session
        await Assert.That(session.World.Template.Name).IsEqualTo("headless_world"); // synthetic world
        await Assert.That(session.ProvisionedAccount).IsNull(); // no managed account row
    }

    // ---------------------------------------------------------------- production path, no DB

    [Test]
    public void Provision_RejectsNonManagedUsername_BeforeAnyDbAccess()
    {
        // A human-style username must fail validation BEFORE the service can
        // touch the database — the provisioning path can never create or
        // adopt a non-bot account even when the DB is up.
        Assert.Throws<ArgumentException>(() => HeadlessSession.Provision("josh", "JoshBot"));
    }

    [Test]
    public void Provision_WithoutMySql_FailsLoudly()
    {
        // Without a database the production path must THROW — never silently
        // fall back to the synthetic fixture (that fallback is exactly the
        // review's correction (b)).
        Assert.Throws<Exception>(() => HeadlessSession.Provision("bot_managed_hermetic_0001", "HermeticBot"));
    }

    [Test]
    public void Provision_RejectsEmptyCharacterName()
    {
        // Character names ride the same NameManager rules as humans; an empty
        // name can never produce a characters row.
        Assert.Throws<ArgumentException>(() => HeadlessSession.Provision("bot_managed_hermetic_0002", ""));
    }

    // ---------------------------------------------------------------- step-3 isolation (closeout)

    [Test]
    public async Task DefaultCreate_GrantsNoHomesteadDesignOrCerts()
    {
        // Ordinary (non-opt-in) provisioning must not silently grant homestead
        // resources. Production Provision is DB-gated (see
        // Provision_WithoutMySql_FailsLoudly), so the observable default path
        // is the DB-free Create fixture.
        SeedFixtureSingletons();
        var session = HeadlessSession.Create(4200002u, "NoKitBot", 1);

        var bag = session.Character.Inventory?.Bag;
        await Assert.That(bag is not null).IsEqualTo(true);
        var snapshot = bag!.GetItemsSnapshot();
        await Assert.That(snapshot.Any(i => i != null &&
            (i.TemplateId == AcquireScarecrowAction.ScarecrowDesignTemplateId ||
             i.TemplateId == AcquireScarecrowAction.ScarecrowDesignId))).IsEqualTo(false);
        await Assert.That(snapshot.Any(i => i != null &&
            i.TemplateId == AcquireScarecrowAction.TaxCertificateTemplateId)).IsEqualTo(false);
    }

    [Test]
    public async Task KitGrant_OnEmptyBag_GrantsExactlyOneDesignAndTheCanonicalCertCount()
    {
        // The explicit opt-in path grants exactly the starter kit — no more, and
        // no fewer than the claim it enables costs: the certificate count must
        // cover one week + deposit of housing 267's canonical tax (50,000 copper
        // per week → 15 certificates at the engine's 10,000-copper denomination).
        SeedFixtureSingletons();
        using var canonical = LoadCanonicalHousingTaxData();
        // Kit templates live only for this test: seed into the live manager and
        // remove afterwards so no other suite ever observes them (run-order isolation).
        var seededTemplates = SeedKitTemplates(ItemManager.Instance);
        try
        {
            var session = HeadlessSession.Create(4200003u, "KitBot", 1);

            HeadlessSession.GrantStarterHomesteadKit(session.Character);

            var bag = session.Character.Inventory?.Bag;
            await Assert.That(bag is not null).IsEqualTo(true);
            var snapshot = bag!.GetItemsSnapshot();
            var designs = snapshot
                .Where(i => i != null && (i.TemplateId == AcquireScarecrowAction.ScarecrowDesignTemplateId ||
                                          i.TemplateId == AcquireScarecrowAction.ScarecrowDesignId))
                .Sum(i => i.Count);
            var certs = snapshot
                .Where(i => i != null && i.TemplateId == AcquireScarecrowAction.TaxCertificateTemplateId)
                .Sum(i => i.Count);
            await Assert.That(designs).IsEqualTo(1);
            await Assert.That(certs).IsEqualTo(AcquireScarecrowAction.RequiredFirstPlacementTaxCertificates());
            await Assert.That(certs).IsEqualTo(15);
        }
        finally
        {
            RemoveKitTemplates(ItemManager.Instance, seededTemplates);
        }
    }

    [Test]
    public async Task KitGrant_RepeatedSetup_GrantsNoDuplicates()
    {
        // Repeated setup is idempotent: the deficit grant adds nothing twice.
        SeedFixtureSingletons();
        using var canonical = LoadCanonicalHousingTaxData();
        var seededTemplates = SeedKitTemplates(ItemManager.Instance);
        try
        {
            var session = HeadlessSession.Create(4200004u, "KitTwiceBot", 1);

            HeadlessSession.GrantStarterHomesteadKit(session.Character);
            HeadlessSession.GrantStarterHomesteadKit(session.Character);

            var bag = session.Character.Inventory?.Bag;
            await Assert.That(bag is not null).IsEqualTo(true);
            var snapshot = bag!.GetItemsSnapshot();
            var designs = snapshot
                .Where(i => i != null && (i.TemplateId == AcquireScarecrowAction.ScarecrowDesignTemplateId ||
                                          i.TemplateId == AcquireScarecrowAction.ScarecrowDesignId))
                .Sum(i => i.Count);
            var certs = snapshot
                .Where(i => i != null && i.TemplateId == AcquireScarecrowAction.TaxCertificateTemplateId)
                .Sum(i => i.Count);
            await Assert.That(designs).IsEqualTo(1);
            await Assert.That(certs).IsEqualTo(AcquireScarecrowAction.RequiredFirstPlacementTaxCertificates());
            await Assert.That(certs).IsEqualTo(15);
        }
        finally
        {
            RemoveKitTemplates(ItemManager.Instance, seededTemplates);
        }
    }

    [Test]
    public async Task HomesteadModule_ByDefault_RefusesActivation()
    {
        // Default eligibility refuses unfinished homestead work: a fresh module
        // denies before touching the bot (CanActivate checks Enabled first).
        SeedFixtureSingletons();
        var session = HeadlessSession.Create(4200005u, "DefaultEligibilityBot", 1);
        var bot = new PlayerBotRuntime(session.Character, "step3-tests");

        var decision = new HomesteadActivityModule().CanActivate(new BotActivityContext { Bot = bot, GameHour = 12f });

        await Assert.That(decision.CanActivate).IsEqualTo(false);
    }

    // ---------------------------------------------------------------- adopt decision (t_db5b2be7)

    [Test]
    public async Task ResolveAdoptable_UnregisteredName_ReturnsZero_CreatePath()
    {
        // A name no one owns is a fresh create — no adoption.
        var nameManager = BuildSeededNameManager([]);

        var result = HeadlessSession.ResolveAdoptableBotCharacterId(nameManager, "citizen01", botAccountId: 14);

        await Assert.That(result).IsEqualTo(0u);
    }

    [Test]
    public async Task ResolveAdoptable_BotOwnedName_ReturnsExistingCharacterId()
    {
        // A prior boot's row: the name is registered under the SAME managed
        // bot account → adopt (reload + re-embody), never a new row.
        var nameManager = BuildSeededNameManager(new Dictionary<uint, (string Name, uint AccountId)>
        {
            [101] = ("citizen01", 14)
        });

        var result = HeadlessSession.ResolveAdoptableBotCharacterId(nameManager, "citizen01", botAccountId: 14);

        await Assert.That(result).IsEqualTo(101u);
    }

    [Test]
    public void ResolveAdoptable_ForeignOwnedName_Throws_SquattingProtection()
    {
        // The name is taken by a HUMAN account (7) — bots never adopt foreign
        // rows; the NameManager duplicate guard stays in force for human
        // names, exactly like the create-only path.
        var nameManager = BuildSeededNameManager(new Dictionary<uint, (string Name, uint AccountId)>
        {
            [202] = ("citizen01", 7)
        });

        Assert.Throws<ArgumentException>(() =>
            HeadlessSession.ResolveAdoptableBotCharacterId(nameManager, "citizen01", botAccountId: 14));
    }

    [Test]
    public void ResolveAdoptable_AnotherBotAccountsName_Throws_NoCrossAdoption()
    {
        // The name is owned by a DIFFERENT managed bot account (15). Same
        // guard as human squatting: adoption is strictly find-by-name+account,
        // so a config change that renames the account prefix surfaces loudly
        // instead of silently re-attaching another bot's character.
        var nameManager = BuildSeededNameManager(new Dictionary<uint, (string Name, uint AccountId)>
        {
            [303] = ("citizen01", 15)
        });

        Assert.Throws<ArgumentException>(() =>
            HeadlessSession.ResolveAdoptableBotCharacterId(nameManager, "citizen01", botAccountId: 14));
    }

    /// <summary>Builds a fresh NameManager seeded from in-memory registries
    /// (the internal Load overload — the same shape NameManager.Load reads
    /// from the characters table at boot).</summary>
    private static NameManager BuildSeededNameManager(Dictionary<uint, (string Name, uint AccountId)> characters)
    {
        var nameManager = new NameManager();
        var ids = new Dictionary<uint, string>();
        var names = new Dictionary<string, uint>();
        var accounts = new Dictionary<uint, uint>();
        foreach (var (id, (characterName, accountId)) in characters)
        {
            ids[id] = characterName;
            names[characterName] = id;
            accounts[id] = accountId;
        }

        nameManager.Load(ids, names, accounts);
        return nameManager;
    }

    // ------------------------------------------------- canonical kit sizing

    /// <summary>
    /// Loads the canonical housing templates + taxations (compact.sqlite3, the same
    /// join the engine performs at boot) so the kit's certificate requirement is
    /// derived from real data instead of a literal. Restores the singleton on dispose.
    /// </summary>
    private static IDisposable LoadCanonicalHousingTaxData()
    {
        var field = typeof(Singleton<AAEmu.Game.GameData.HousingGameData>)
            .GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)!;
        var previous = field.GetValue(null);

        var gameData = new AAEmu.Game.GameData.HousingGameData();
        using (var connection = new SqliteConnection($"Data Source={CanonicalDbPath};Mode=ReadOnly"))
        {
            connection.Open();
            gameData.Load(connection);
            if (!GameplayActorTestRig.SingletonSeeded(typeof(Singleton<TaxationsManager>)))
            {
                var taxations = new TaxationsManager { taxations = new Dictionary<uint, Taxation>() };
                using var cmd = connection.CreateCommand();
                cmd.CommandText = "SELECT id, tax FROM taxations";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    taxations.taxations[Convert.ToUInt32(reader.GetValue(0))] = new Taxation
                    {
                        Id = Convert.ToUInt32(reader.GetValue(0)),
                        Tax = Convert.ToUInt32(reader.GetValue(1))
                    };
                }
                GameplayActorTestRig.SeedSingleton(typeof(Singleton<TaxationsManager>), taxations);
            }
        }
        gameData.PostLoad();
        field.SetValue(null, gameData);

        return new RestoreSingleton(() => field.SetValue(null, previous));
    }

    private sealed class RestoreSingleton(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }

    private static string CanonicalDbPath
    {
        get
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            foreach (var candidate in new[]
                     {
                         Path.Combine(baseDir, "..", "..", "..", "..", "AAEmu.Game", "Data", "compact.sqlite3"),
                         Path.Combine(Directory.GetCurrentDirectory(), "AAEmu.Game", "Data", "compact.sqlite3")
                     })
            {
                if (File.Exists(candidate))
                    return candidate;
            }
            throw new FileNotFoundException("compact.sqlite3 not found in any expected test layout");
        }
    }

    // ---------------------------------------------------------------- singleton seeding

    /// <summary>
    /// Seeds exactly the singletons HeadlessSession.Create resolves
    /// (Inventory ctor → ContainerIdManager.Instance.GetNextId +
    /// ItemManager.GetItemContainerForCharacter) with missing-only guards.
    ///
    /// NEVER call PlayerbotPilotRig.SeedPilotSingletons() from a new rig: its
    /// one-shot s_seeded flag flips full-suite ordering (t_4f11a519) — a
    /// scenario rig replacing QuestManager afterwards then NREs later pilot
    /// probes. Seeding is per-singleton, never replaces an established
    /// singleton, and never touches the pilot flag.
    private static void SeedFixtureSingletons()
    {
        SetSingletonIfMissing(typeof(Singleton<ItemManager>), BuildFixtureItemManager());
        // Inventory.OnAcquiredItem fires QuestManager.DoItemsAcquiredEvents —
        // seed a data-free manager (M3a convention) so the hook is a no-op.
        SetSingletonIfMissing(typeof(Singleton<QuestManager>),
            new QuestManager(Mock.Of<ITaskManager>().Object, Mock.Of<IZoneManager>().Object));
        // Initialize WITHOUT force-reset: Initialize(true) rewinds the container-ID
        // sequence, so a later test's fresh bag reuses an earlier test's ContainerId
        // and GetOrAdd hands back the old (possibly kit-filled) container — cross-test
        // bag sharing that breaks run-order independence. First-init-wins keeps every
        // container id in this process unique, matching the missing-only discipline above.
        ContainerIdManager.Instance.Initialize(false);
    }

    private static ItemManager BuildFixtureItemManager()
    {
        var itemIdManager = Mock.Of<IItemIdManager>();
        var itemManager = new ItemManager(
            Mock.Of<ISkillManager>().Object,
            itemIdManager.Object,
            Mock.Of<IContainerIdManager>().Object,
            Mock.Of<ILocalizationManager>().Object,
            Mock.Of<ITaskManager>().Object,
            Mock.Of<IWorldManager>().Object);

        // The Inventory ctor resolves ItemManager.GetItemContainerForCharacter,
        // which iterates _allPersistentContainers. Scenario-rig ItemManagers
        // never seed it (their inventory bypasses ItemManager) — a null
        // registry would NRE the ordinary Character construction path.
        var containerField = typeof(ItemManager).GetField("_allPersistentContainers",
            BindingFlags.NonPublic | BindingFlags.Instance);
        var existing = containerField?.GetValue(itemManager) as ConcurrentDictionary<ulong, ItemContainer>;
        if (existing == null)
            containerField?.SetValue(itemManager, new ConcurrentDictionary<ulong, ItemContainer>());

        // GetNewId locks _removedItems, and Create registers in _allItems —
        // both initialized only by Load. Seed empty registries.
        var removedField = typeof(ItemManager).GetField("_removedItems",
            BindingFlags.NonPublic | BindingFlags.Instance);
        if (removedField?.GetValue(itemManager) == null)
            removedField?.SetValue(itemManager, new List<ulong>());
        var allItemsField = typeof(ItemManager).GetField("_allItems",
            BindingFlags.NonPublic | BindingFlags.Instance);
        if (allItemsField?.GetValue(itemManager) == null)
            allItemsField?.SetValue(itemManager, new ConcurrentDictionary<ulong, Item>());

        // Item creation takes runtime ids from the id manager (stub default 0
        // would mint degenerate id-0 items) — serve incrementing fixture ids.
        uint nextItemId = 9_000_001;
        itemIdManager.GetNextId().Returns(() => nextItemId++);

        return itemManager;
    }

    /// <summary>Seeds the two starter-kit templates into the given (live) manager.
    /// Returns the ids this call added, so the caller can remove exactly those
    /// afterwards — pre-existing entries are never touched.</summary>
    private static List<uint> SeedKitTemplates(ItemManager itemManager)
    {
        // The kit grant resolves templates by id (ItemManager.GetTemplate over
        // _templates, never Loaded in a rig). Seed exactly the two starter-kit
        // templates so AcquireDefaultItem exercises its real stacking path.
        var added = new List<uint>();
        var templatesField = typeof(ItemManager).GetField("_templates",
            BindingFlags.NonPublic | BindingFlags.Instance);
        var templates = templatesField?.GetValue(itemManager) as Dictionary<uint, ItemTemplate>;
        if (templates == null)
        {
            templates = new Dictionary<uint, ItemTemplate>();
            templatesField?.SetValue(itemManager, templates);
        }
        if (templates.TryAdd(AcquireScarecrowAction.ScarecrowDesignTemplateId,
            new ItemTemplate { Id = AcquireScarecrowAction.ScarecrowDesignTemplateId, MaxCount = 1 }))
            added.Add(AcquireScarecrowAction.ScarecrowDesignTemplateId);
        if (templates.TryAdd(AcquireScarecrowAction.TaxCertificateTemplateId,
            new ItemTemplate { Id = AcquireScarecrowAction.TaxCertificateTemplateId, MaxCount = 100 }))
            added.Add(AcquireScarecrowAction.TaxCertificateTemplateId);
        return added;
    }

    private static void RemoveKitTemplates(ItemManager itemManager, List<uint> addedTemplateIds)
    {
        if (addedTemplateIds.Count == 0)
            return;
        var templates = typeof(ItemManager).GetField("_templates",
            BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(itemManager) as Dictionary<uint, ItemTemplate>;
        foreach (var id in addedTemplateIds)
            templates?.Remove(id);
    }

    private static void SetSingletonIfMissing(Type singletonBase, object instance)
    {
        var field = singletonBase.GetField("s_instance",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException($"Cannot locate singleton field on {singletonBase.Name}");
        if (field.GetValue(null) != null)
            return; // never replace an established singleton (t_4f11a519)
        field.SetValue(null, instance);
    }
}
